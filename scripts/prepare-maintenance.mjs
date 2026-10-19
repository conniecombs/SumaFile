import { runRecipe } from './lib/repair.mjs';
runRecipe(process.env.MAINTENANCE_RECIPE, process.env.RELEASE_VERSION);
