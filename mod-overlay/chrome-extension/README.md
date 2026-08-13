# ShareX-Mod Chrome Enhanced Companion (v0.4)

This unpacked Manifest V3 extension is the browser-aware capture path for ShareX-Mod v0.4. It uses Chrome's official `chrome.debugger` transport to drive CDP without launching Chrome with `--remote-debugging-port`.

## Install

1. Open `chrome://extensions` in Chrome, Edge, Brave, or another Chromium browser.
2. Enable **Developer mode**.
3. Choose **Load unpacked** and select this `ShareX-Mod.ChromeExtension` folder from the ShareX-Mod portable package.
4. Pin **ShareX-Mod Chrome Enhanced** to the toolbar.

Chrome displays the standard debugger permission warning because this extension uses the official debugger API.

## Capture workflow

1. Open the page you want to capture.
2. Optional: click **Pick start element**, then click the first element that should be included.
3. Optional: click **Pick end element**, then click the last element that should be included.
4. Click **Start background capture**.
5. The page may remain in a background tab while the extension progressively loads content, expands safe hidden sections, chooses natural cut boundaries, captures segmented PNG parts, and optionally downloads original/high-resolution images.
6. Output is written to `Downloads/ShareX-Mod/ChromeCaptures/<timestamp>-<title>/`.

If no end anchor is marked, capture continues until the document bottom remains stable for several passes. This supports pages whose height grows while lazy content is loaded.

## v0.4 behavior

- Keeps an active `chrome.debugger` session for the whole capture so Chrome 118+ keeps the MV3 service worker alive.
- Captures document-coordinate tiles using `Page.captureScreenshot` with `captureBeyondViewport`, so the tab does not need to stay foreground.
- Progressively scrolls the page before each tile to trigger lazy loading and waits for nearby images plus a short settle delay.
- Picks nearby DOM element boundaries to reduce cuts through paragraphs, images, tables, code blocks, and forum posts.
- Expands `<details>`, common forum spoiler blocks, and nested vertical scroll containers, then restores the page afterward.
- Best-effort auto-attaches to out-of-process child frames for the same preparation logic.
- Saves a JSON manifest describing every part, CSS range, scaling factor, anchors, preparation counts, and image appendix results.
- Can save large original image assets separately when their natural resolution exceeds their rendered size.

## Limits / safety

- Chrome internal pages (`chrome://`, `edge://`, extension pages, DevTools) cannot be captured through the debugger API.
- Opening DevTools on the same tab can detach another debugger client. Close DevTools on that tab before starting a capture.
- Some highly virtualized applications remove content far outside the viewport. v0.4 mitigates this by warming each region immediately before capture and keeping tiles relatively short, but extreme virtualization can still require smaller target part heights.
- Page mutations are restored on normal completion/cancel. A browser crash can prevent cleanup; reloading the page restores its original DOM.
