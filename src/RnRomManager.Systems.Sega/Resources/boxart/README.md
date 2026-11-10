# Default box art

`gamegear.svg`, `mastersystem.svg` and `megadrive.svg` are the sources (made for this project, MIT). The embedded `gamegear.png`,
`mastersystem.png` and `megadrive.png` are 512×512 renders of them (spec §6.1). After editing an SVG, re-export it with headless Edge or Chrome:

```bash
msedge --headless=new --hide-scrollbars --force-device-scale-factor=1 --window-size=512,512 --screenshot=gamegear.png file:///<full path>/gamegear.svg
```
