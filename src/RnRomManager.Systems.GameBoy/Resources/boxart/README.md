# Default box art

`gb.svg` / `gbc.svg` / `gba.svg` are the sources (made for this project, MIT). The embedded `gb.png` / `gbc.png` / `gba.png` are
512×512 renders of them (spec §6.1). After editing an SVG, re-export it with headless Edge or Chrome:

```bash
msedge --headless=new --hide-scrollbars --force-device-scale-factor=1 --window-size=512,512 --screenshot=gb.png file:///<full path>/gb.svg
```
