# Default box art

`a2600.svg`, `a5200.svg`, `a7800.svg`, `lynx.svg` and `jaguar.svg` are the sources (made for this project, MIT).
The embedded PNGs are 512×512 renders (spec §6.1). After editing an SVG, re-export it with headless Edge or Chrome:

```bash
msedge --headless=new --hide-scrollbars --force-device-scale-factor=1 --window-size=512,512 --screenshot=a2600.png file:///<full path>/a2600.svg
```
