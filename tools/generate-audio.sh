#!/usr/bin/env bash
# Generates the waiting-hall voice clips (Arabic + English) used by the display screen.
#   Requires: espeak-ng, ffmpeg   (Ubuntu: sudo apt install espeak-ng ffmpeg)
#   Usage:    tools/generate-audio.sh [output-dir]
#
# The synthetic voice is intelligible but robotic. For a better result the hospital can record a human
# voice and replace any file with the same name (mp3, mono). Structure:
#   chime.mp3, pause.mp3
#   ar/number.mp3  ("الرقم")            en/number.mp3  ("Number")
#   ar/room.mp3    ("الرجاء التوجه إلى غرفة")   en/room.mp3 ("please go to room")
#   ar/letters/A.mp3 … Z.mp3            en/letters/A.mp3 … Z.mp3
#   ar/n/1.mp3 … 999.mp3                en/n/1.mp3 … 999.mp3
set -euo pipefail
OUT="${1:-$(dirname "$0")/../src/JIH.Queue.Web/wwwroot/audio}"
mkdir -p "$OUT"/{ar,en}/{n,letters}
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

enc() { # wav → trimmed mono mp3
  ffmpeg -loglevel error -y -i "$1" -af "silenceremove=start_periods=1:start_threshold=-45dB,areverse,silenceremove=start_periods=1:start_threshold=-45dB,areverse" \
    -ac 1 -ar 16000 -b:a 24k "$2"
}
say() { # lang text out
  local voice=$1 text=$2 out=$3 wav
  wav=$(mktemp "$TMP/clip.XXXXXX.wav")
  for attempt in 1 2 3; do
    espeak-ng -v "$voice" -s 135 -w "$wav" "$text" && enc "$wav" "$out" && break
  done
  rm -f "$wav"
}
export -f enc say
export TMP

# chime (two soft tones) and a short pause
ffmpeg -loglevel error -y -f lavfi -i "sine=frequency=880:duration=0.35" -f lavfi -i "sine=frequency=660:duration=0.6" \
  -filter_complex "[0]afade=t=out:st=0.2:d=0.15[a];[1]afade=t=out:st=0.2:d=0.4[b];[a][b]concat=n=2:v=0:a=1,volume=0.6" -ac 1 -b:a 64k "$OUT/chime.mp3"
ffmpeg -loglevel error -y -f lavfi -i "anullsrc=r=22050:cl=mono" -t 0.45 -b:a 32k "$OUT/pause.mp3"

say ar "الرقم" "$OUT/ar/number.mp3"
say ar "الرجاء التوجه إلى غرفة" "$OUT/ar/room.mp3"
say en-us "Number" "$OUT/en/number.mp3"
say en-us "please go to room" "$OUT/en/room.mp3"

# Latin letters as Arabic speakers pronounce them
declare -A AR=( [A]="إيه" [B]="بي" [C]="سي" [D]="دي" [E]="إي" [F]="إف" [G]="جي" [H]="إتش" [I]="آي" [J]="جيه" [K]="كيه" [L]="إل" [M]="إم"
                [N]="إن" [O]="أو" [P]="بيه" [Q]="كيو" [R]="آر" [S]="إس" [T]="تي" [U]="يو" [V]="في" [W]="دبليو" [X]="إكس" [Y]="واي" [Z]="زد" )
for L in "${!AR[@]}"; do
  say ar "${AR[$L]}" "$OUT/ar/letters/$L.mp3"
  say en-us "$L" "$OUT/en/letters/$L.mp3"
done

# numbers 1–999 (ticket numbers and room numbers)
seq 1 999 | xargs -P "$(nproc)" -I{} bash -c 'say ar "{}" "'"$OUT"'/ar/n/{}.mp3"; say en-us "{}" "'"$OUT"'/en/n/{}.mp3"'
echo "Clips written to $OUT"
