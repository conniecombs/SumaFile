//! Windows only: one frame of a video, through Media Foundation.
//!
//! Frames are read with the source reader in the decoder's own pixel format,
//! with the reader's converters off: a frame that is only stepped over is
//! never converted. The one chosen frame is converted and scaled by Windows'
//! video processor, on the graphics card when there is a device.
//!
//! A frame the graphics card decoded arrives as a Direct3D texture; a frame
//! the processor decoded arrives in ordinary memory. That, and nothing else,
//! decides what a thumbnail reports: Windows decodes on the processor
//! without any error when it has no hardware decoder for a file.

use crate::attempt::{self, Device, Step};
use crate::pixels::upright_rgb;
use crate::rules::{self, Area, Turn};
use image::{DynamicImage, RgbImage};
use std::mem::ManuallyDrop;
use std::path::Path;
use std::sync::{Mutex, OnceLock, PoisonError};
use std::time::Instant;
use sumafile_imaging::thumbnail::{fit, render_decoded, Decoded};
use sumafile_media::render::RenderError;
use sumafile_media::server::Answered;
use sumafile_media_client::protocol::ImageKind;
use sumafile_media_client::Rendered;
use windows::core::{Error, Interface, Result, GUID, HSTRING};
use windows::Win32::Foundation::{E_FAIL, HMODULE, RECT};
use windows::Win32::Graphics::Direct3D::D3D_DRIVER_TYPE_HARDWARE;
use windows::Win32::Graphics::Direct3D11::{
    D3D11CreateDevice, ID3D11Device, ID3D11Multithread, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
    D3D11_CREATE_DEVICE_VIDEO_SUPPORT, D3D11_SDK_VERSION,
};
use windows::Win32::Graphics::Dxgi::{
    IDXGIAdapter, IDXGIAdapter1, IDXGIDevice, DXGI_ADAPTER_FLAG_SOFTWARE,
};
use windows::Win32::Media::MediaFoundation::{
    CLSID_VideoProcessorMFT, IMF2DBuffer, IMFAttributes, IMFDXGIBuffer, IMFDXGIDeviceManager,
    IMFMediaType, IMFSample, IMFSourceReader, IMFTransform, IMFVideoProcessorControl,
    MFCreate2DMediaBuffer, MFCreateAttributes, MFCreateDXGIDeviceManager, MFCreateMediaType,
    MFCreateSample, MFCreateSourceReaderFromURL, MFMediaType_Video, MFStartup,
    MFVideoFormat_ARGB32, MFVideoFormat_AYUV, MFVideoFormat_I420, MFVideoFormat_IYUV,
    MFVideoFormat_NV12, MFVideoFormat_P010, MFVideoFormat_RGB24, MFVideoFormat_RGB32,
    MFVideoFormat_RGB555, MFVideoFormat_RGB565, MFVideoFormat_UYVY, MFVideoFormat_YUY2,
    MFVideoFormat_YV12, MFVideoFormat_YVYU, MFVideoInterlace_Progressive, MFSTARTUP_LITE,
    MFT_MESSAGE_COMMAND_DRAIN, MFT_MESSAGE_NOTIFY_BEGIN_STREAMING,
    MFT_MESSAGE_NOTIFY_START_OF_STREAM, MFT_MESSAGE_SET_D3D_MANAGER, MFT_OUTPUT_DATA_BUFFER,
    MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES, MFT_OUTPUT_STREAM_PROVIDES_SAMPLES,
    MF_E_TRANSFORM_NEED_MORE_INPUT, MF_MT_ALL_SAMPLES_INDEPENDENT, MF_MT_DEFAULT_STRIDE,
    MF_MT_FRAME_SIZE, MF_MT_GEOMETRIC_APERTURE, MF_MT_INTERLACE_MODE, MF_MT_MAJOR_TYPE,
    MF_MT_MINIMUM_DISPLAY_APERTURE, MF_MT_PIXEL_ASPECT_RATIO, MF_MT_SUBTYPE, MF_MT_VIDEO_ROTATION,
    MF_PD_DURATION, MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, MF_SOURCE_READERF_ENDOFSTREAM,
    MF_SOURCE_READERF_ERROR, MF_SOURCE_READER_ALL_STREAMS, MF_SOURCE_READER_D3D_MANAGER,
    MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, MF_SOURCE_READER_FIRST_VIDEO_STREAM,
    MF_SOURCE_READER_MEDIASOURCE, MF_VERSION, ROTATION_NONE,
};
use windows::Win32::System::Com::StructuredStorage::PROPVARIANT;
use windows::Win32::System::Com::{CoCreateInstance, CoIncrementMTAUsage, CLSCTX_INPROC_SERVER};

const VIDEO_STREAM: u32 = MF_SOURCE_READER_FIRST_VIDEO_STREAM.0 as u32;
const ALL_STREAMS: u32 = MF_SOURCE_READER_ALL_STREAMS.0 as u32;
const MEDIA_SOURCE: u32 = MF_SOURCE_READER_MEDIASOURCE.0 as u32;
/// `D3DFMT_X8R8G8B8`: 32 bits a pixel as blue, green, red, unused.
const X8R8G8B8: u32 = 22;
/// The pixel formats a file's frames may be stored in without compression,
/// each of which the video processor takes as input. Decided by the format,
/// not by asking the type whether it is compressed: that answer follows
/// another attribute, which a source may leave unset.
const UNCOMPRESSED: [GUID; 13] = [
    MFVideoFormat_RGB24,
    MFVideoFormat_RGB32,
    MFVideoFormat_ARGB32,
    MFVideoFormat_RGB555,
    MFVideoFormat_RGB565,
    MFVideoFormat_NV12,
    MFVideoFormat_YUY2,
    MFVideoFormat_UYVY,
    MFVideoFormat_YVYU,
    MFVideoFormat_YV12,
    MFVideoFormat_I420,
    MFVideoFormat_IYUV,
    MFVideoFormat_AYUV,
];
/// A reader that keeps answering with neither a frame nor an end is given up.
const MOST_READS_WITHOUT_A_FRAME: u32 = 1000;

/// A JPEG of the frame `position_percent` of the way through the video at
/// `path`, fitted into a `box_side` box, and who decoded it.
pub(crate) fn thumbnail(
    path: &Path,
    box_side: u32,
    position_percent: u8,
    hardware: bool,
) -> std::result::Result<Answered, RenderError> {
    std::fs::metadata(path)
        .map_err(|error| RenderError::Io(format!("Cannot read {}: {error}", path.display())))?;
    if !media_foundation_started() {
        return Err(RenderError::Unsupported(
            "Media Foundation could not be started.".to_string(),
        ));
    }
    let url = HSTRING::from(path.as_os_str());

    let manager = if hardware { device_manager() } else { None };
    let device = if manager.is_some() {
        Device::Ready
    } else {
        Device::Missing
    };
    let mut first_step_failed = false;
    if attempt::first_step(hardware, device) == Step::WithDevice {
        // SAFETY: Media Foundation is started, and `manager` holds a device.
        match unsafe { frame(&url, box_side, position_percent, manager.as_ref()) } {
            Ok(frame) => {
                return answer(frame, box_side, hardware, device, Step::WithDevice, false);
            }
            Err(_) => {
                first_step_failed = true;
                forget_a_lost_device();
            }
        }
    }
    // SAFETY: Media Foundation is started.
    let frame = unsafe { frame(&url, box_side, position_percent, None) }
        .map_err(|_| RenderError::Decode("The video could not be decoded.".to_string()))?;
    answer(
        frame,
        box_side,
        hardware,
        device,
        Step::WithoutDevice,
        first_step_failed,
    )
}

/// The frame chosen, upright, at the size the video processor made it.
struct Frame {
    picture: RgbImage,
    /// The decoder handed it out as a Direct3D texture.
    on_graphics_card: bool,
}

fn answer(
    frame: Frame,
    box_side: u32,
    hardware: bool,
    device: Device,
    step: Step,
    first_step_failed: bool,
) -> std::result::Result<Answered, RenderError> {
    let video = attempt::report(
        hardware,
        device,
        step,
        first_step_failed,
        frame.on_graphics_card,
    );
    let (width, height) = frame.picture.dimensions();
    // A video smaller than the box is not enlarged.
    let side = box_side.min(width.max(height));
    let (thumbnail_width, thumbnail_height) = fit(width, height, side);
    let decoded = Decoded {
        image: DynamicImage::ImageRgb8(frame.picture),
        full_size: (width, height),
    };
    let bytes = render_decoded(&decoded, side).map_err(RenderError::Decode)?;
    Ok(Answered {
        rendered: Rendered {
            kind: ImageKind::Jpeg,
            width: thumbnail_width,
            height: thumbnail_height,
            bytes,
        },
        video: Some(video),
    })
}

/// Start COM and Media Foundation, once for the life of the process. Neither
/// is shut down: the process ends instead.
fn media_foundation_started() -> bool {
    static STARTED: OnceLock<bool> = OnceLock::new();
    *STARTED.get_or_init(|| {
        // SAFETY: both calls take no pointers of ours. The usage count keeps
        // a multithreaded apartment alive for the process, so the request
        // threads, which never initialise COM themselves, are in it.
        unsafe { CoIncrementMTAUsage().is_ok() && MFStartup(MF_VERSION, MFSTARTUP_LITE).is_ok() }
    })
}

/// The graphics device video is decoded with, and its manager.
struct Graphics {
    device: ID3D11Device,
    manager: IMFDXGIDeviceManager,
}

// SAFETY: a Direct3D 11 device and a DXGI device manager may be used from
// any thread. The device has multithread protection turned on, and Media
// Foundation itself hands both to its worker threads.
unsafe impl Send for Graphics {}

enum DeviceState {
    Untried,
    /// Windows has no graphics device that can decode video. Not tried again
    /// for the life of the process.
    Missing,
    Ready(Graphics),
}

static DEVICE: Mutex<DeviceState> = Mutex::new(DeviceState::Untried);

/// The device manager, made on the first request that may use it.
fn device_manager() -> Option<IMFDXGIDeviceManager> {
    let mut state = DEVICE.lock().unwrap_or_else(PoisonError::into_inner);
    if matches!(*state, DeviceState::Untried) {
        // SAFETY: Media Foundation is started; the call owns what it makes.
        *state = match unsafe { create_graphics() } {
            Ok(graphics) => DeviceState::Ready(graphics),
            Err(_) => DeviceState::Missing,
        };
    }
    match &*state {
        DeviceState::Ready(graphics) => Some(graphics.manager.clone()),
        DeviceState::Untried | DeviceState::Missing => None,
    }
}

/// After a failed attempt: a device the driver has taken away (a driver
/// reset, a card removed) is dropped, and a new one is made on the next
/// request.
fn forget_a_lost_device() {
    let mut state = DEVICE.lock().unwrap_or_else(PoisonError::into_inner);
    let lost = match &*state {
        // SAFETY: the device is a live COM object owned by `state`.
        DeviceState::Ready(graphics) => {
            unsafe { graphics.device.GetDeviceRemovedReason() }.is_err()
        }
        DeviceState::Untried | DeviceState::Missing => false,
    };
    if lost {
        *state = DeviceState::Untried;
    }
}

fn failed() -> Error {
    Error::from(E_FAIL)
}

/// A Direct3D 11 device that can decode video, on Windows' default adapter.
///
/// # Safety
/// Media Foundation must be started.
unsafe fn create_graphics() -> Result<Graphics> {
    let mut device: Option<ID3D11Device> = None;
    // SAFETY: `device` outlives the call, which writes one interface to it.
    unsafe {
        D3D11CreateDevice(
            None::<&IDXGIAdapter>,
            D3D_DRIVER_TYPE_HARDWARE,
            HMODULE::default(),
            D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            None,
            D3D11_SDK_VERSION,
            Some(&mut device),
            None,
            None,
        )?;
    }
    let device = device.ok_or_else(failed)?;

    // A software adapter (Microsoft's Basic Render Driver) would only
    // pretend to be a graphics card.
    // SAFETY: plain calls on live COM objects.
    let description = unsafe {
        device
            .cast::<IDXGIDevice>()?
            .GetAdapter()?
            .cast::<IDXGIAdapter1>()?
            .GetDesc1()?
    };
    if description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE.0 as u32 != 0 {
        return Err(failed());
    }

    // Media Foundation decodes on its own threads.
    // SAFETY: plain calls on live COM objects.
    unsafe {
        let _ = device
            .cast::<ID3D11Multithread>()?
            .SetMultithreadProtected(true);
    }

    let mut reset_token = 0u32;
    let mut manager: Option<IMFDXGIDeviceManager> = None;
    // SAFETY: both out-parameters outlive the call.
    unsafe { MFCreateDXGIDeviceManager(&mut reset_token, &mut manager)? };
    let manager = manager.ok_or_else(failed)?;
    // SAFETY: the device is live, and the token is the manager's own.
    unsafe { manager.ResetDevice(&device, reset_token)? };
    Ok(Graphics { device, manager })
}

/// Two 32-bit numbers as Media Foundation packs them into 64 bits.
fn pack(high: u32, low: u32) -> u64 {
    u64::from(high) << 32 | u64::from(low)
}

fn unpack(packed: u64) -> (u32, u32) {
    ((packed >> 32) as u32, packed as u32)
}

/// A reader over the first video stream of the file at `url`.
/// `own_converter`: the reader converts every frame to 32-bit RGB itself.
///
/// # Safety
/// Media Foundation must be started.
unsafe fn open_reader(
    url: &HSTRING,
    manager: Option<&IMFDXGIDeviceManager>,
    own_converter: bool,
) -> Result<IMFSourceReader> {
    let mut attributes: Option<IMFAttributes> = None;
    // SAFETY: the out-parameter outlives the call.
    unsafe { MFCreateAttributes(&mut attributes, 3)? };
    let attributes = attributes.ok_or_else(failed)?;
    // SAFETY: plain calls on live COM objects; the keys are constants.
    unsafe {
        if let Some(manager) = manager {
            attributes.SetUnknown(&MF_SOURCE_READER_D3D_MANAGER, manager)?;
            attributes.SetUINT32(&MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1)?;
        }
        if own_converter {
            attributes.SetUINT32(&MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1)?;
        }
        let reader = MFCreateSourceReaderFromURL(url, &attributes)?;
        reader.SetStreamSelection(ALL_STREAMS, false)?;
        reader.SetStreamSelection(VIDEO_STREAM, true)?;
        Ok(reader)
    }
}

/// Ask `reader` for uncompressed frames of the first of `subtypes` it can
/// give.
///
/// # Safety
/// `reader` must be a live source reader.
unsafe fn ask_for(reader: &IMFSourceReader, subtypes: &[GUID]) -> bool {
    subtypes.iter().any(|subtype| {
        // SAFETY: plain calls on live COM objects; the keys are constants.
        unsafe {
            MFCreateMediaType()
                .and_then(|wanted| {
                    wanted.SetGUID(&MF_MT_MAJOR_TYPE, &MFMediaType_Video)?;
                    wanted.SetGUID(&MF_MT_SUBTYPE, subtype)?;
                    reader.SetCurrentMediaType(VIDEO_STREAM, None, &wanted)
                })
                .is_ok()
        }
    })
}

/// The next frame of `reader` and its time; `None` at the end of the stream.
///
/// # Safety
/// `reader` must be a live source reader.
unsafe fn next_frame(reader: &IMFSourceReader) -> Result<Option<(i64, IMFSample)>> {
    for _ in 0..MOST_READS_WITHOUT_A_FRAME {
        let (mut flags, mut time, mut sample) = (0u32, 0i64, None);
        // SAFETY: the three out-parameters outlive the call.
        unsafe {
            reader.ReadSample(
                VIDEO_STREAM,
                0,
                None,
                Some(&mut flags),
                Some(&mut time),
                Some(&mut sample),
            )?;
        }
        if flags & MF_SOURCE_READERF_ERROR.0 as u32 != 0 {
            return Err(failed());
        }
        if let Some(sample) = sample {
            return Ok(Some((time, sample)));
        }
        if flags & MF_SOURCE_READERF_ENDOFSTREAM.0 as u32 != 0 {
            return Ok(None);
        }
        // A gap in the stream, or a change of format: read on.
    }
    Err(failed())
}

/// One attempt: the frame `position_percent` of the way through the video,
/// with the graphics device when `manager` is given.
///
/// # Safety
/// Media Foundation must be started.
unsafe fn frame(
    url: &HSTRING,
    box_side: u32,
    position_percent: u8,
    manager: Option<&IMFDXGIDeviceManager>,
) -> Result<Frame> {
    // SAFETY: Media Foundation is started; every call below is on a live
    // COM object this function owns, with constant keys.
    unsafe {
        let mut reader = open_reader(url, manager, false)?;
        let native = reader.GetNativeMediaType(VIDEO_STREAM, 0)?;

        // A file whose frames are not compressed keeps its own format: the
        // video processor takes it as it is. Otherwise frames are asked for
        // in the decoder's own format: what Windows' decoders give,
        // commonest first.
        let native_subtype = native.GetGUID(&MF_MT_SUBTYPE).unwrap_or(GUID::zeroed());
        let mut asked = UNCOMPRESSED.contains(&native_subtype)
            && reader
                .SetCurrentMediaType(VIDEO_STREAM, None, &native)
                .is_ok();
        if !asked {
            asked = ask_for(
                &reader,
                &[MFVideoFormat_NV12, MFVideoFormat_P010, MFVideoFormat_YUY2],
            );
        }
        if !asked && manager.is_none() {
            // A decoder that offers none of these: the reader's own
            // converter, which converts every frame it steps over.
            reader = open_reader(url, None, true)?;
            asked = ask_for(&reader, &[MFVideoFormat_RGB32]);
        }
        if !asked {
            return Err(failed());
        }

        let duration = reader
            .GetPresentationAttribute(MEDIA_SOURCE, &MF_PD_DURATION)
            .ok()
            .and_then(|value| u64::try_from(&value).ok())
            .unwrap_or(0);
        let position =
            i64::try_from(rules::position_for(duration, position_percent)).map_err(|_| failed())?;
        if position > 0 {
            // A source that cannot seek is read from its start.
            let _ = reader.SetCurrentPosition(&GUID::zeroed(), &PROPVARIANT::from(position));
        }

        let began = Instant::now();
        let sample = rules::choose_frame(|| next_frame(&reader), position, || began.elapsed())?
            .ok_or_else(failed)?;
        let on_graphics_card = sample.GetBufferByIndex(0)?.cast::<IMFDXGIBuffer>().is_ok();

        // Read after the frames: the format may have changed on the way.
        let current = reader.GetCurrentMediaType(VIDEO_STREAM)?;
        let frame_size = unpack(current.GetUINT64(&MF_MT_FRAME_SIZE)?);
        // The part of the frame that is picture: a decoder pads the frame to
        // its block size, and the padding is not to be shown.
        let picture_area = rules::visible_area(frame_size, stated_area(&current));
        let pixel_aspect = current
            .GetUINT64(&MF_MT_PIXEL_ASPECT_RATIO)
            .map(unpack)
            .unwrap_or((1, 1));
        let rotation = current
            .GetUINT32(&MF_MT_VIDEO_ROTATION)
            .or_else(|_| native.GetUINT32(&MF_MT_VIDEO_ROTATION))
            .unwrap_or(0);
        let (width, height) = rules::processor_size(
            (picture_area.width, picture_area.height),
            pixel_aspect,
            box_side,
        );

        // The device is for a frame that is on the graphics card. A frame
        // the processor decoded sits in ordinary memory and is converted
        // there: nothing is gained by sending it to the card and back.
        let converter = manager.filter(|_| on_graphics_card);
        let whole_frame = (picture_area.width, picture_area.height) == frame_size;
        let source = (!whole_frame).then_some(picture_area);
        let converted = convert(&sample, &current, source, width, height, converter)?;
        let picture = copy_out(&converted, width, height, Turn::from_degrees(rotation))?;
        Ok(Frame {
            picture,
            on_graphics_card,
        })
    }
}

/// The area a video type states as its picture, when it states one.
///
/// # Safety
/// `video_type` must be a live media type.
unsafe fn stated_area(video_type: &IMFMediaType) -> Option<Area> {
    [MF_MT_MINIMUM_DISPLAY_APERTURE, MF_MT_GEOMETRIC_APERTURE]
        .iter()
        .find_map(|key| {
            let mut blob = [0u8; 16];
            let mut length = 0u32;
            // SAFETY: the buffer and the length outlive the call, which
            // writes no more than the buffer holds.
            unsafe { video_type.GetBlob(key, &mut blob, Some(&mut length)) }.ok()?;
            rules::area_from_blob(blob.get(..length as usize)?)
        })
}

/// `sample`, a frame of type `input`, as 32-bit RGB at `width` by `height`,
/// through Windows' video processor: on the graphics card when `manager` is
/// given, on the processor otherwise. `source` is the part of the frame to
/// take, when that is not all of it. The frame is not turned here.
///
/// # Safety
/// Media Foundation must be started, and `sample` and `input` live.
unsafe fn convert(
    sample: &IMFSample,
    input: &IMFMediaType,
    source: Option<Area>,
    width: u32,
    height: u32,
    manager: Option<&IMFDXGIDeviceManager>,
) -> Result<IMFSample> {
    // SAFETY: every call is on a live COM object, with constant keys. The
    // manager's pointer is passed as the message's parameter, as the message
    // is defined, and outlives the processor's use of it within this call.
    unsafe {
        let processor: IMFTransform =
            CoCreateInstance(&CLSID_VideoProcessorMFT, None, CLSCTX_INPROC_SERVER)?;
        if let Some(manager) = manager {
            processor.ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, manager.as_raw() as usize)?;
        }
        processor.SetInputType(0, input, 0)?;
        if let Ok(control) = processor.cast::<IMFVideoProcessorControl>() {
            // Turning is done by this program's own code, which is tested.
            let _ = control.SetRotation(ROTATION_NONE);
            if let Some(area) = source {
                let part = RECT {
                    left: area.x as i32,
                    top: area.y as i32,
                    right: (area.x + area.width) as i32,
                    bottom: (area.y + area.height) as i32,
                };
                let _ = control.SetSourceRectangle(Some(&part));
            }
        }

        // 32-bit RGB, with or without the word "alpha": the bytes are the
        // same, and a processor may offer only one of the two.
        let mut output_set = Err(failed());
        for subtype in [MFVideoFormat_RGB32, MFVideoFormat_ARGB32] {
            let output = MFCreateMediaType()?;
            output.SetGUID(&MF_MT_MAJOR_TYPE, &MFMediaType_Video)?;
            output.SetGUID(&MF_MT_SUBTYPE, &subtype)?;
            output.SetUINT64(&MF_MT_FRAME_SIZE, pack(width, height))?;
            output.SetUINT64(&MF_MT_PIXEL_ASPECT_RATIO, pack(1, 1))?;
            output.SetUINT32(&MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive.0 as u32)?;
            output.SetUINT32(&MF_MT_ALL_SAMPLES_INDEPENDENT, 1)?;
            // Top row first, said outright: an RGB picture in ordinary
            // memory is otherwise bottom row first by habit.
            output.SetUINT32(&MF_MT_DEFAULT_STRIDE, width * 4)?;
            output_set = processor.SetOutputType(0, &output, 0);
            if output_set.is_ok() {
                break;
            }
        }
        output_set?;

        processor.ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0)?;
        processor.ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0)?;
        processor.ProcessInput(0, sample, 0)?;

        let stream = processor.GetOutputStreamInfo(0)?;
        let provides_samples = stream.dwFlags
            & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES.0 | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES.0)
                as u32
            != 0;
        for drained in [false, true] {
            let ours = if provides_samples {
                None
            } else {
                // A buffer whose top row comes first, whatever the format's
                // habit: `copy_out` then reads it the same way every time.
                let ours = MFCreateSample()?;
                ours.AddBuffer(&MFCreate2DMediaBuffer(width, height, X8R8G8B8, false)?)?;
                Some(ours)
            };
            let mut buffers = [MFT_OUTPUT_DATA_BUFFER {
                dwStreamID: 0,
                pSample: ManuallyDrop::new(ours),
                dwStatus: 0,
                pEvents: ManuallyDrop::new(None),
            }];
            let mut status = 0u32;
            let outcome = processor.ProcessOutput(0, &mut buffers, &mut status);
            let [buffer] = buffers;
            // Taken out of the structure so that both are released.
            let made = ManuallyDrop::into_inner(buffer.pSample);
            drop(ManuallyDrop::into_inner(buffer.pEvents));
            match outcome {
                Ok(()) => return made.ok_or_else(failed),
                Err(error) if error.code() == MF_E_TRANSFORM_NEED_MORE_INPUT && !drained => {
                    // No more input is coming: it has all it will get.
                    processor.ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0)?;
                }
                Err(error) => return Err(error),
            }
        }
        Err(failed())
    }
}

/// The pixels of `sample`, a frame of 32-bit RGB at `width` by `height`,
/// copied to ordinary memory and turned upright.
///
/// # Safety
/// `sample` must be a live sample holding such a frame.
unsafe fn copy_out(sample: &IMFSample, width: u32, height: u32, turn: Turn) -> Result<RgbImage> {
    let row_bytes = width as usize * 4;
    let rows = height as usize;
    // SAFETY: the buffer is locked for exactly as long as it is read, and
    // what is read lies inside it, as said at each read below.
    unsafe {
        let buffer = sample.ConvertToContiguousBuffer()?;
        if let Ok(image) = buffer.cast::<IMF2DBuffer>() {
            let (mut top, mut pitch) = (std::ptr::null_mut::<u8>(), 0i32);
            image.Lock2D(&mut top, &mut pitch)?;
            // `top` is the first byte of the top row, and each next row is
            // `pitch` bytes on: back in memory when the pitch is negative.
            // A locked 2D buffer holds `rows` such rows of at least
            // `row_bytes` bytes.
            let mut copied = Vec::with_capacity(row_bytes * rows);
            if !top.is_null() && pitch.unsigned_abs() as usize >= row_bytes {
                for row in 0..rows as isize {
                    let start = top.offset(row * pitch as isize);
                    copied.extend_from_slice(std::slice::from_raw_parts(start, row_bytes));
                }
            }
            let _ = image.Unlock2D();
            return upright_rgb(&copied, row_bytes as i32, width, height, turn).ok_or_else(failed);
        }

        // A buffer without rows of its own: one block, in the order and with
        // the row length the format's default stride says.
        let (mut data, mut length) = (std::ptr::null_mut::<u8>(), 0u32);
        buffer.Lock(&mut data, None, Some(&mut length))?;
        let copied = if data.is_null() {
            Vec::new()
        } else {
            // `Lock` gives `length` readable bytes at `data`.
            std::slice::from_raw_parts(data, length as usize).to_vec()
        };
        let _ = buffer.Unlock();
        // The output type declares the top row first (`convert`).
        upright_rgb(&copied, row_bytes as i32, width, height, turn).ok_or_else(failed)
    }
}
