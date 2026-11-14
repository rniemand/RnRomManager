#!/bin/sh
# Rebuilds nds-marker.nds in the pinned devkitARM image (devkitARM r68, GCC 16.1.0, libnds 2.0.2, calico 1.2.0,
# ndstool 2.3.1). The build runs in a copy under /tmp, so the output doesn't depend on the checkout's path.
set -eu
IMAGE=devkitpro/devkitarm@sha256:116afba8df8453961de2936ffab20dd441edf4d682856c1ec8b0e53d7ed0bbf5
cd "$(dirname "$0")"
docker run --rm -v "$PWD:/src" "$IMAGE" sh -c \
	'cp -r /src/source /src/Makefile /tmp/ && cd /tmp && make && cp nds-marker.nds /src/nds-marker.nds'
sha1sum nds-marker.nds
