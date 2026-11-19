# Retro terminal fonts

Decision: USE the two requested/provided fonts and ADAPT the existing WinForms drawing code. No UI framework or font service is added.

## White Rabbit

- Provided as `white-rabbit.zip` after the Agyx Mono Rounded file could not be found locally.
- Family: White Rabbit, by Matthew Welch (1999).
- Used for navigation, address entry, tabs, history, status and small start-page text.
- Archive contained only `whitrabt.ttf`, `license.txt` and `whitrabt.txt`; both text files were read.
- The included MIT-style license allows use, modification and redistribution with its notice retained. Exact original license and readme are included alongside the font.
- Archive SHA-256: `CD107EFFC11E870C172FAB8415461FB28C6842091659B89FA31C2C04B85D0D17`.
- Font SHA-256: `3E845AF724F2916D7DB7A0565C52C0FCFB0D57ED9615D3640707C6EEB5B1CAF7`.

## Skynet College

- Family: Skynet College, by Woodcutter.
- Used for ARABARA branding; the detailed display face is kept out of the address field and small controls.
- Retrieved from the [author's DaFont listing](https://www.dafont.com/skynet-college.font), using its download link.
- The archive contained only `Skynet College.ttf`. The author's page allows personal use; see `SkynetCollege-NOTICE.txt` for source and terms.
- Archive SHA-256: `F32E0531CEF13B2DEB255C33ECC11F8772A6A7D42EF5E5914F1F8B0284A6C7A8`.
- Font SHA-256: `D1A0E555C1EF4535EFBF0D7057E1342B402C5B30EEB3610F5B72AE4DB8D15983`.

## Loading and verification

Only the two named files are loaded, once per application process. The build checks the pinned hashes and copies the fonts and notices into `bin/fonts`. GDI private registration supports native text controls; GDI+ private fonts support the painted start page. Both registrations are released on exit. No fonts are installed in Windows or fetched while browsing. Missing font files fall back to Consolas; the smoke test requires the intended families to be present.

The archive contents and metadata were inspected; no installer or executable script was present. This was not a font-parser security audit. The reuse-first Python helper was unavailable on PATH, so this decision is recorded manually.

Agyx Mono Rounded was located on the [publisher's MyFonts page](https://www.myfonts.com/collections/agyx-mono-rounded-font-fitrah-type), but was not installed or substituted under that name. The user-supplied White Rabbit is the UI font in this build.

Implementation references: [AddFontResourceEx / FR_PRIVATE](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-addfontresourceexw), [PrivateFontCollection.AddFontFile](https://learn.microsoft.com/en-us/dotnet/api/system.drawing.text.privatefontcollection.addfontfile).
