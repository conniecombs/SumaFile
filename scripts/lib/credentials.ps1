#requires -Version 5.1
# Generic Windows Credential Manager targets for the smokes, which must clean up what the app under test wrote even
# when the app itself crashed or was killed. Windows PowerShell 5.1 has no Credential Manager cmdlet, so this goes
# through advapi32 directly. Dot-source it: `. scripts/lib/credentials.ps1`.

if (-not ('SumaFile.Smoke.CredentialNative' -as [type])) {
    Add-Type -Namespace SumaFile.Smoke -Name CredentialNative -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
[return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
public static extern bool CredEnumerateW(string filter, int flags, out int count, out System.IntPtr credentials);

[System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
[return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
public static extern bool CredDeleteW(string target, int type, int flags);

[System.Runtime.InteropServices.DllImport("advapi32.dll")]
public static extern void CredFree(System.IntPtr buffer);

public const int CRED_TYPE_GENERIC = 1;
public const int ERROR_NOT_FOUND = 1168;

// The generic targets whose names start with prefix (CredEnumerateW's "prefix*" filter).
public static string[] GenericTargets(string prefix)
{
    int count;
    System.IntPtr credentials;
    if (!CredEnumerateW(prefix + "*", 0, out count, out credentials))
    {
        int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        if (error == ERROR_NOT_FOUND) { return new string[0]; }
        throw new System.ComponentModel.Win32Exception(error, "CredEnumerateW(" + prefix + "*) failed: " + error);
    }

    try
    {
        var targets = new System.Collections.Generic.List<string>();
        for (int index = 0; index < count; index++)
        {
            // CREDENTIALW: DWORD Flags; DWORD Type; LPWSTR TargetName; ...
            System.IntPtr credential = System.Runtime.InteropServices.Marshal.ReadIntPtr(credentials, index * System.IntPtr.Size);
            if (System.Runtime.InteropServices.Marshal.ReadInt32(credential, 4) != CRED_TYPE_GENERIC) { continue; }
            string target = System.Runtime.InteropServices.Marshal.PtrToStringUni(
                System.Runtime.InteropServices.Marshal.ReadIntPtr(credential, 8));
            if (target != null) { targets.Add(target); }
        }

        return targets.ToArray();
    }
    finally
    {
        CredFree(credentials);
    }
}

// Deletes one generic target; one that is already gone is fine.
public static void DeleteGeneric(string target)
{
    if (!CredDeleteW(target, CRED_TYPE_GENERIC, 0))
    {
        int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        if (error != ERROR_NOT_FOUND)
        {
            throw new System.ComponentModel.Win32Exception(error, "CredDeleteW(" + target + ") failed: " + error);
        }
    }
}
'@
}

function Get-GenericCredentialTargets {
    # The generic Credential Manager targets whose names start with -Prefix, as a string[] (empty when none).
    [CmdletBinding()]
    [OutputType([string[]])]
    param([Parameter(Mandatory = $true)][string]$Prefix)
    if ($Prefix.Contains('*')) { throw "The credential prefix must not hold a wildcard: $Prefix" }
    $targets = [SumaFile.Smoke.CredentialNative]::GenericTargets($Prefix)
    return , [string[]]$targets
}

function Remove-GenericCredential {
    # Deletes one generic Credential Manager target by its exact name. A target that is already gone is fine.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Target)
    if ($Target.Contains('*')) { throw "A credential target to delete must be exact, not a pattern: $Target" }
    [SumaFile.Smoke.CredentialNative]::DeleteGeneric($Target)
}
