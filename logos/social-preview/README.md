# Social preview image

`social-preview.html` is the source of `../GraphBridge-social-preview.png`, the 1280×640 image GitHub shows when the repository link is shared. The logo is the unmodified `../GraphBridge-logo.png`, scaled only.

To regenerate it, render the page at exactly 1280×640 with a headless Chromium browser, from the repository root:

```powershell
& "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe" --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 --window-size=1280,640 --screenshot="$PWD\logos\GraphBridge-social-preview.png" "file:///$($PWD.Path.Replace('\','/'))/logos/social-preview/social-preview.html"
```

Keep the result under 1 MB and keep the text inside the central 1100×540 area. Upload it in the repository's **Settings → General → Social preview**; GitHub has no API for this.
