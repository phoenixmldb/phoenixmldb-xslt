// Runs the published browser-wasm app under Node: no browser and no wasm workload needed.
import { dotnet } from './_framework/dotnet.js';
const { runMain } = await dotnet.create();
process.exit(await runMain());
