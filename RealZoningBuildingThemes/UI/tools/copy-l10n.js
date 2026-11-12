// Copies the translation files next to the built panel, so the C# side finds them in the
// deployed mod folder (Mods\RealZoningBuildingThemes\l10n). Run after webpack.
const fs = require("fs");
const path = require("path");
const from = path.join(__dirname, "..", "l10n");
const to = path.join(__dirname, "..", "build", "l10n");
fs.mkdirSync(to, { recursive: true });
let n = 0;
for (const f of fs.readdirSync(from)) {
  if (f.endsWith(".json")) {
    JSON.parse(fs.readFileSync(path.join(from, f), "utf8")); // fail loudly on a broken file
    fs.copyFileSync(path.join(from, f), path.join(to, f));
    n++;
  }
}
console.log(`Copied ${n} translation files to build/l10n`);
