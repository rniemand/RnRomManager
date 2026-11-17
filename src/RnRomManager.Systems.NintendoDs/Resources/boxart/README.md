# Default box art

`nds.svg` and `3ds.svg` are the sources (made for this project, MIT).
The embedded PNGs are 512×512 renders (spec §6.1). After editing the SVG, re-export it with headless Edge or Chrome:

```bash
msedge --headless=new --hide-scrollbars --force-device-scale-factor=1 --window-size=512,512 --screenshot=nds.png file:///<full path>/nds.svg
```
