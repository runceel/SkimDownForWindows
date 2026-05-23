// Generate Windows shell icons from AppIcon-source.png (the upstream SkimDown
// master icon, used with permission from @07JP27).
//
// Outputs (overwriting existing files in src/SkimDownForWindows/Assets/):
//
//   PNG (centered on transparent background where the asset has spare area):
//     AppIcon.png                                              128x128   (in-app TitleBar icon)
//     Square44x44Logo.scale-200.png                            88x88
//     Square44x44Logo.targetsize-24_altform-unplated.png       24x24
//     Square44x44Logo.targetsize-48_altform-lightunplated.png  48x48
//     Square150x150Logo.scale-200.png                          300x300
//     LockScreenLogo.scale-200.png                             48x48
//     StoreLogo.png                                            50x50
//
//   PNG with transparent background (wide / splash) — icon centered at 75% of
//   the tile height; OS supplies the tile colour:
//     Wide310x150Logo.scale-200.png                            620x300
//     SplashScreen.scale-200.png                               1240x600
//
//   ICO (multi-resolution): 16, 24, 32, 48, 64, 128, 256
//     AppIcon.ico
//
// All sizes downscale from the same square master (1254x1254). Sharp uses
// lanczos3 by default which produces clean results down to 16x16 because the
// upstream icon is gradient-dominated.

const fs = require("fs");
const path = require("path");
const sharp = require("sharp");
const pngToIco = require("png-to-ico").default;

const ASSETS = path.resolve(__dirname, "..", "src/SkimDownForWindows/Assets");
const SOURCE = path.join(ASSETS, "AppIcon-source.png");

if (!fs.existsSync(SOURCE)) {
    console.error("FATAL: missing source icon at", SOURCE);
    console.error("Copy the upstream icon.png from https://github.com/07JP27/SkimDown to that path.");
    process.exit(1);
}

async function rasterSquare(size, outPath) {
    const buf = await sharp(SOURCE)
        .resize(size, size, { fit: "contain", background: { r: 0, g: 0, b: 0, alpha: 0 } })
        .png()
        .toBuffer();
    fs.writeFileSync(outPath, buf);
    console.log("  wrote", path.basename(outPath), `(${size}x${size})`);
}

async function rasterWide(width, height, outPath) {
    // Center the square icon at ~75% of the tile height on a transparent
    // canvas. Keeps proportions consistent across wide / splash variants.
    const iconSize = Math.round(height * 0.75);
    const iconBuf = await sharp(SOURCE)
        .resize(iconSize, iconSize, { fit: "contain", background: { r: 0, g: 0, b: 0, alpha: 0 } })
        .png()
        .toBuffer();

    const left = Math.round((width - iconSize) / 2);
    const top = Math.round((height - iconSize) / 2);

    const composed = await sharp({
        create: { width, height, channels: 4, background: { r: 0, g: 0, b: 0, alpha: 0 } },
    })
        .composite([{ input: iconBuf, top, left }])
        .png()
        .toBuffer();

    fs.writeFileSync(outPath, composed);
    console.log("  wrote", path.basename(outPath), `(${width}x${height})`);
}

async function rasterIcoFrame(size) {
    return sharp(SOURCE)
        .resize(size, size, { fit: "contain", background: { r: 0, g: 0, b: 0, alpha: 0 } })
        .png()
        .toBuffer();
}

async function main() {
    console.log("Generating Windows shell icons from", SOURCE);

    console.log("\n[Square PNG]");
    await rasterSquare(128, path.join(ASSETS, "AppIcon.png"));
    await rasterSquare(88,  path.join(ASSETS, "Square44x44Logo.scale-200.png"));
    await rasterSquare(24,  path.join(ASSETS, "Square44x44Logo.targetsize-24_altform-unplated.png"));
    await rasterSquare(48,  path.join(ASSETS, "Square44x44Logo.targetsize-48_altform-lightunplated.png"));
    await rasterSquare(300, path.join(ASSETS, "Square150x150Logo.scale-200.png"));
    await rasterSquare(48,  path.join(ASSETS, "LockScreenLogo.scale-200.png"));
    await rasterSquare(50,  path.join(ASSETS, "StoreLogo.png"));

    console.log("\n[Wide / Splash PNG]");
    await rasterWide(620, 300, path.join(ASSETS, "Wide310x150Logo.scale-200.png"));
    await rasterWide(1240, 600, path.join(ASSETS, "SplashScreen.scale-200.png"));

    console.log("\n[ICO multi-res frames]");
    const icoSizes = [16, 24, 32, 48, 64, 128, 256];
    const tmpFiles = [];
    for (const s of icoSizes) {
        const tmp = path.join(ASSETS, `__ico_${s}.png`);
        fs.writeFileSync(tmp, await rasterIcoFrame(s));
        tmpFiles.push(tmp);
        console.log(`  prepared frame ${s}x${s}`);
    }
    const ico = await pngToIco(tmpFiles);
    fs.writeFileSync(path.join(ASSETS, "AppIcon.ico"), ico);
    console.log("  wrote AppIcon.ico (multi-res)");

    for (const f of tmpFiles) fs.unlinkSync(f);
    console.log("\nDone.");
}

main().catch(e => { console.error("FATAL:", e); process.exit(1); });
