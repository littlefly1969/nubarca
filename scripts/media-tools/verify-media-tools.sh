#!/usr/bin/env bash
# Proves the media tools do what NubArca asks of them, on the base the API runs on.
#
# Not "is ffmpeg there" but "does each thing NubArca does with it work":
# the HLS ladder, the poster, the preview strip, scene detection, ffprobe's
# JSON, every legacy format the library holds that FFmpeg can also write, the
# iPhone's tiled and rotated HEIC, and HDR (HLG, 10-bit) converted to SDR.
# Exits non-zero on the first thing that does not.
set -euo pipefail

BIN="${MEDIA_TOOLS_BIN:-/opt/nubarca/media/bin}"
FIXTURES="${MEDIA_TOOLS_FIXTURES:-/opt/nubarca/media-fixtures}"
FFMPEG="$BIN/ffmpeg"
FFPROBE="$BIN/ffprobe"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

ok() { printf '  ok    %s\n' "$1"; }
fail() { printf '  FAIL  %s\n' "$1" >&2; exit 1; }
ff() { "$FFMPEG" -hide_banner -nostdin -v error -y "$@"; }
probe() { "$FFPROBE" -v error "$@"; }

echo "NubArca media tools"
[ -x "$FFMPEG" ] && [ -x "$FFPROBE" ] || fail "ffmpeg and ffprobe present in $BIN"
"$FFMPEG" -hide_banner -version | head -1

# --- linkage: nothing missing on this base -----------------------------------
linkage="$(ldd "$FFMPEG" "$FFPROBE")"
if grep -q 'not found' <<< "$linkage"; then
  ldd "$FFMPEG" "$FFPROBE" | grep 'not found' >&2
  fail "every shared library resolves"
fi
ok "every shared library resolves"

# --- the components NubArca names --------------------------------------------
# The lists are read once into files: piping ffmpeg into `grep -q` under
# pipefail fails on the SIGPIPE grep's early exit, not on a missing component.
for kind in decoders encoders demuxers muxers filters; do
  "$FFMPEG" -hide_banner -"$kind" > "$WORK/$kind.txt" 2>/dev/null
done
"$FFMPEG" -hide_banner -protocols 2>/dev/null | sed -n '/Input:/,/Output:/p' | sed 's/^ *//' > "$WORK/protocols.txt"
has() { # has <kind> <name>: a decoder/encoder/demuxer/muxer/filter/protocol is built in
  local kind="$1" name="$2"
  case "$kind" in
    decoder|encoder) awk '{print $2}' "$WORK/${kind}s.txt" | grep -qx -- "$name" ;;
    demuxer|muxer) awk 'NR>4 {print $2}' "$WORK/${kind}s.txt" | tr ',' '\n' | grep -qx -- "$name" ;;
    filter) awk '{print $2}' "$WORK/filters.txt" | grep -qx -- "$name" ;;
    protocol) grep -qx -- "$name" "$WORK/protocols.txt" ;;
  esac
}
for d in h264 hevc mpeg4 msmpeg4v2 msmpeg4 h263 mjpeg cfhd dvvideo vp8 vp9 libdav1d prores \
         aac mp3 mp2 wmav2 ac3 amrnb pcm_u8 pcm_s16le opus flac; do
  has decoder "$d" || fail "decoder $d"
done
ok "decoders: video (h264 hevc mpeg4 msmpeg4 h263 mjpeg cfhd dv vp8 vp9 av1/dav1d prores) and audio"
for d in mov mp4 avi matroska asf dv mpegts flv; do has demuxer "$d" || fail "demuxer $d"; done
ok "demuxers: mov/mp4 (also HEIF), avi, matroska, asf, dv, mpegts, flv"
for m in hls image2 null mp4; do has muxer "$m" || fail "muxer $m"; done
for e in libx264 aac mjpeg png; do has encoder "$e" || fail "encoder $e"; done
for f in scale setsar split crop gblur overlay hstack select metadata zscale tonemap format; do
  has filter "$f" || fail "filter $f"
done
ok "muxers, encoders and filters NubArca's commands use"
for p in http https tcp udp rtmp; do
  if has protocol "$p"; then fail "no network protocol ($p is built in)"; fi
done
has protocol file || fail "file protocol"
ok "no network protocols: local files only"

# --- what NubArca does with them -----------------------------------------------
ff -f lavfi -i "testsrc2=size=1280x720:rate=30" -f lavfi -i "sine=frequency=440:sample_rate=48000" \
   -t 4 -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest "$WORK/source.mp4"
ok "x264 + AAC encode"

mkdir -p "$WORK/hls"
ff -i "$WORK/source.mp4" -map 0:v:0 -map 0:a:0 -map 0:v:0 -map 0:a:0 \
   -filter:v:0 "scale=w='if(gt(a,1),-2,min(1080,iw))':h='if(gt(a,1),min(1080,ih),-2)'" \
   -c:v:0 libx264 -preset:v:0 veryfast -pix_fmt:v:0 yuv420p -crf:v:0 23 \
   -filter:v:1 "scale=w='if(gt(a,1),-2,480)':h='if(gt(a,1),480,-2)'" \
   -c:v:1 libx264 -preset:v:1 veryfast -pix_fmt:v:1 yuv420p -crf:v:1 28 \
   -c:a:0 aac -c:a:1 aac -f hls -hls_time 2 -hls_playlist_type vod -hls_segment_type fmp4 \
   -hls_fmp4_init_filename init.mp4 -hls_segment_filename "$WORK/hls/v%v/s%03d.m4s" \
   -master_pl_name master.m3u8 -var_stream_map "v:0,a:0 v:1,a:1" "$WORK/hls/v%v/index.m3u8"
[ -s "$WORK/hls/master.m3u8" ] || fail "HLS master playlist"
ok "HLS ladder (two renditions, fMP4)"

ff -ss 1 -i "$WORK/source.mp4" -frames:v 1 -an \
   -vf "scale=640:640:force_original_aspect_ratio=decrease,setsar=1" -f image2 -q:v 2 "$WORK/poster.jpg"
[ -s "$WORK/poster.jpg" ] || fail "poster"
ok "poster frame (JPEG)"

ff -ss 0.5 -i "$WORK/source.mp4" -ss 1.5 -i "$WORK/source.mp4" -filter_complex \
   "[0:v]split=2[bg0][fg0];[bg0]scale=160:90:force_original_aspect_ratio=increase,crop=160:90,gblur=sigma=10[back0];[fg0]scale=160:90:force_original_aspect_ratio=decrease[front0];[back0][front0]overlay=(W-w)/2:(H-h)/2,setsar=1[cell0];[1:v]split=2[bg1][fg1];[bg1]scale=160:90:force_original_aspect_ratio=increase,crop=160:90,gblur=sigma=10[back1];[fg1]scale=160:90:force_original_aspect_ratio=decrease[front1];[back1][front1]overlay=(W-w)/2:(H-h)/2,setsar=1[cell1];[cell0][cell1]hstack=inputs=2[strip]" \
   -map "[strip]" -frames:v 1 -an -f image2 -q:v 3 "$WORK/strip.jpg"
[ -s "$WORK/strip.jpg" ] || fail "preview strip"
ok "preview strip (split, crop, gblur, overlay, hstack)"

"$FFMPEG" -hide_banner -nostdin -v error -i "$WORK/source.mp4" -an -sn \
   -filter:v "select='gt(scene,0.3)',metadata=print:file=-" -f null - > /dev/null
ok "scene detection (select + metadata)"

json="$(probe -print_format json -show_format -show_streams "$WORK/source.mp4")"
grep -q '"codec_name": "h264"' <<< "$json" || fail "ffprobe JSON"
ok "ffprobe JSON"

# Every legacy pairing the library holds that FFmpeg can also write: encoded
# here, then decoded back. (AV1, VP9 and AMR have no encoder in this build:
# their decoders are asserted above.)
legacy() { # legacy <label> <file> <encode args...>
  local label="$1" file="$2"; shift 2
  ff -f lavfi -i "testsrc2=size=320x240:rate=15" -f lavfi -i "sine=sample_rate=44100" -t 1 "$@" "$WORK/$file"
  ff -i "$WORK/$file" -f null - || fail "decode $label"
}
legacy "mpeg4 avi" a.avi -c:v mpeg4 -c:a mp2
legacy "msmpeg4v3 avi" b.avi -c:v msmpeg4v3 -c:a pcm_s16le
legacy "h263 3gp" c.3gp -c:v h263 -s 352x288 -c:a aac -ar 8000 -ac 1
legacy "mjpeg + pcm_u8 mov" d.mov -c:v mjpeg -c:a pcm_u8
legacy "dv" e.dv -c:v dvvideo -s 720x576 -pix_fmt yuv420p -r 25 -c:a pcm_s16le -ar 48000 -ac 2
legacy "wmav2 asf" f.asf -c:v msmpeg4v2 -c:a wmav2
legacy "ac3 mkv" g.mkv -c:v mpeg4 -c:a ac3
legacy "cfhd mov" h.mov -c:v cfhd -pix_fmt yuv422p10le -c:a pcm_s16le
ok "legacy formats round-trip (mpeg4, msmpeg4, h263, mjpeg, dv, wmav2, ac3, cfhd)"

# The iPhone's photo format: a grid of 256x256 tiles, rotated 90 degrees in the
# container. The still must come out whole and upright: 600x800.
ff -i "$FIXTURES/iphone-like-grid-rotated.heic" -frames:v 1 "$WORK/heic.png"
dims="$(probe -select_streams v:0 -show_entries stream=width,height -of csv=p=0:s=x "$WORK/heic.png")"
[ "$dims" = "600x800" ] || fail "HEIC grid + rotation decodes upright (got $dims, want 600x800)"
ok "HEIC: tiled grid assembled and rotation applied (600x800)"

# HDR as an iPhone records it (HEVC 10-bit, HLG, BT.2020), converted to SDR
# BT.709 8-bit — what HLS, posters and previews must show instead of grey —
# by the chain the API builds for it (VideoColorFormat).
transfer="$(probe -select_streams v:0 -show_entries stream=color_transfer -of csv=p=0 "$FIXTURES/hdr-hlg-10bit.mov")"
[ "$transfer" = "arib-std-b67" ] || fail "HDR fixture reads as HLG (got $transfer)"
ff -i "$FIXTURES/hdr-hlg-10bit.mov" \
   -vf "zscale=tin=arib-std-b67:pin=bt2020:min=bt2020nc:rin=tv:t=linear:p=bt2020:npl=203,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=mobius:param=0.5:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p" \
   -c:v libx264 -color_primaries bt709 -color_trc bt709 -colorspace bt709 -frames:v 10 "$WORK/sdr.mp4"
sdr="$(probe -select_streams v:0 -show_entries stream=pix_fmt,color_transfer -of csv=p=0 "$WORK/sdr.mp4")"
[ "$sdr" = "yuv420p,bt709" ] || fail "HDR to SDR (got $sdr)"
ok "HDR (HLG 10-bit) tone-mapped to SDR BT.709"

echo "MEDIA TOOLS VERIFIED"
