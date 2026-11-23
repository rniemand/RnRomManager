# Default box art

`snes.svg`, `satellaview.svg` and `sufami.svg` are the sources (made for this project, MIT). Each embedded PNG is a
512×512 render of its SVG (spec §6.1). After editing an SVG, re-export it with headless Edge or Chrome:

```bash
msedge --headless=new --hide-scrollbars --force-device-scale-factor=1 --window-size=512,512 --screenshot=snes.png file:///<full path>/snes.svg
```
