# Default box art

`ngp.svg` and `ngpc.svg` are the sources (made for this project, MIT).
The embedded PNGs are 512×512 renders (spec §6.1). After editing an SVG, re-export it with headless Edge or Chrome:

```bash
msedge --headless=new --hide-scrollbars --force-device-scale-factor=1 --window-size=512,512 --screenshot=ngp.png file:///<full path>/ngp.svg
msedge --headless=new --hide-scrollbars --force-device-scale-factor=1 --window-size=512,512 --screenshot=ngpc.png file:///<full path>/ngpc.svg
```
