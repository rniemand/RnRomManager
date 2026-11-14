# Default box art

`vb.svg` is the source (made for this project, MIT).
The embedded PNG is a 512×512 render (spec §6.1). After editing the SVG, re-export it with headless Edge or Chrome:

```bash
msedge --headless=new --hide-scrollbars --force-device-scale-factor=1 --window-size=512,512 --screenshot=vb.png file:///<full path>/vb.svg
```
