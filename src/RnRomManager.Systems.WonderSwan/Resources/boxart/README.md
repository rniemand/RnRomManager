# Default box art

`ws.svg`, `wsc.svg` and `pc2.svg` are the sources (made for this project, MIT).
The embedded PNGs are 512×512 renders (spec §6.1). After editing an SVG, re-export it with headless Edge or Chrome:

```bash
msedge --headless=new --hide-scrollbars --force-device-scale-factor=1 --window-size=512,512 --screenshot=ws.png file:///<full path>/ws.svg
msedge --headless=new --hide-scrollbars --force-device-scale-factor=1 --window-size=512,512 --screenshot=wsc.png file:///<full path>/wsc.svg
msedge --headless=new --hide-scrollbars --force-device-scale-factor=1 --window-size=512,512 --screenshot=pc2.png file:///<full path>/pc2.svg
```
