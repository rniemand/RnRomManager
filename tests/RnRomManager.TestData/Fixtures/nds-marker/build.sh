#!/bin/sh
# Rebuilds nds-marker.nds in the pinned devkitARM image (devkitARM r64, GCC 14.1.0, libnds 1.8.2, default-arm7 0.7.4,
# ndstool 2.3.1). The build runs in a copy under /tmp, so the output doesn't depend on the checkout's path.
# libnds 2 (calico) builds don't boot in the DeSmuME core RnRomManager ships, so this stays on the 2024-05-11 image.
set -eu
IMAGE=devkitpro/devkitarm@sha256:c582c1e1e0c72649ea51fcc22ff5f8e5d7565d90fecbc862ce214b9e47f6087a
cd "$(dirname "$0")"
docker run --rm -v "$PWD:/src" "$IMAGE" sh -c \
	'cp -r /src/source /src/Makefile /tmp/ && cd /tmp && make && cp nds-marker.nds /src/nds-marker.nds'
sha1sum nds-marker.nds
