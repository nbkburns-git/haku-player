#!/bin/bash
# Add Haku's vf_hakusr filter to an mpv source tree (run inside WSL): copies the filter, registers it next to vf_d3d11vpp
# and adds it to the same meson block (built whenever d3d-hwaccel is). Safe to run again.
# Usage: apply.sh <mpv source dir>
set -e
M=${1:?mpv source dir}
HERE=$(cd "$(dirname "$0")" && pwd)
cp "$HERE/vf_hakusr.c" "$M/video/filter/vf_hakusr.c"
grep -q "vf_hakusr" "$M/filters/user_filters.h" || \
  sed -i 's|^extern const struct mp_user_filter_entry vf_d3d11vpp;|&\nextern const struct mp_user_filter_entry vf_hakusr;|' "$M/filters/user_filters.h"
grep -q "&vf_hakusr" "$M/filters/user_filters.c" || \
  sed -i 's|^    &vf_d3d11vpp,|&\n    \&vf_hakusr,|' "$M/filters/user_filters.c"
grep -q "vf_hakusr.c" "$M/meson.build" || \
  sed -i "s|                     'video/filter/vf_d3d11vpp.c')|                     'video/filter/vf_d3d11vpp.c',\n                     'video/filter/vf_hakusr.c')|" "$M/meson.build"
grep -n "hakusr" "$M/filters/user_filters.h" "$M/filters/user_filters.c" "$M/meson.build"
