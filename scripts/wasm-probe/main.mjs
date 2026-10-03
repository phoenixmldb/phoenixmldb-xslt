// Runs the published browser-wasm app under Node: no browser and no wasm workload needed.
// Arguments after main.mjs are passed to Program.cs (e.g. "deep-recursion").
import { dotnet } from './_framework/dotnet.js';
const { runMain } = await dotnet.withApplicationArguments(...process.argv.slice(2)).create();
process.exit(await runMain());
