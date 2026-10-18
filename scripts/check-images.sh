#!/bin/sh
# SPDX-License-Identifier: Apache-2.0
#
# Rejects images, RAW files and ISO-BMFF media (HEIC/AVIF/CR3/video) outside
# tests/data/public/ (each file there must be listed in LICENSES.md) and the
# exact paths listed in .githooks/allowed-images.txt.
# Detection uses both the file extension and the file signature (magic bytes),
# so a renamed photo is caught as well. Content is read from the git index.
#
# Usage:
#   scripts/check-images.sh          check staged files (pre-commit hook)
#   scripts/check-images.sh --all    check every tracked file (CI)

set -u

PUBLIC_DIR="tests/data/public/"
LICENSES_FILE="tests/data/public/LICENSES.md"
ALLOW_FILE=".githooks/allowed-images.txt"

if [ "${1:-}" = "--all" ]; then
    files=$(git -c core.quotepath=off ls-files --cached)
else
    files=$(git -c core.quotepath=off diff --cached --name-only --diff-filter=ACMR)
fi

[ -z "$files" ] && exit 0

# Index content of a file, or empty when absent.
index_content() {
    git cat-file blob ":$1" 2>/dev/null
}

allowed_list=$(index_content "$ALLOW_FILE" | sed -e 's/#.*$//' -e 's/[[:space:]]*$//' -e '/^$/d')
licenses_text=$(index_content "$LICENSES_FILE")

extension_kind() {
    lower=$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]')
    case "$lower" in
        *.jpg|*.jpeg|*.jpe|*.jfif|*.png|*.gif|*.webp|*.bmp|*.tif|*.tiff|*.heic|*.heif|*.avif|*.jxl|*.psd|*.ico)
            echo "image extension" ;;
        *.nef|*.nrw|*.cr2|*.cr3|*.crw|*.arw|*.srf|*.sr2|*.raf|*.dng|*.orf|*.rw2|*.pef|*.srw|*.x3f|*.3fr|*.iiq|*.rwl|*.erf|*.mef|*.mos|*.kdc|*.mrw|*.raw)
            echo "RAW extension" ;;
        *.mp4|*.mov|*.m4v|*.avi|*.mts|*.m2ts)
            echo "video extension" ;;
        *) echo "" ;;
    esac
}

signature_kind() {
    hex=$(index_content "$1" | head -c 16 | od -An -tx1 | tr -d ' \n')
    case "$hex" in
        ffd8ff*) echo "JPEG signature" ;;
        89504e470d0a1a0a*) echo "PNG signature" ;;
        47494638*) echo "GIF signature" ;;
        52494646????????57454250*) echo "WebP signature" ;;
        424d????????00000000*) echo "BMP signature" ;;
        49492a00*|4d4d002a*|49492b00*|4d4d002b*) echo "TIFF/RAW signature" ;;
        49495253*|4949524f*|49495500*) echo "RAW (ORF/RW2) signature" ;;
        46554a4946494c4d*) echo "RAW (RAF) signature" ;;
        49491a00*|464f5662*|004d524d*) echo "RAW (CRW/X3F/MRW) signature" ;;
        ????????66747970*) echo "ISO-BMFF (HEIC/AVIF/CR3/video) signature" ;;
        ff0a*|0000000c4a584c20*) echo "JPEG XL signature" ;;
        38425053*) echo "PSD signature" ;;
        00000100*) echo "ICO signature" ;;
        *) echo "" ;;
    esac
}

is_allowed() {
    path=$1
    if [ -n "$allowed_list" ] && printf '%s\n' "$allowed_list" | grep -qxF -- "$path"; then
        return 0
    fi
    case "$path" in
        "$PUBLIC_DIR"*)
            rel=${path#"$PUBLIC_DIR"}
            [ "$path" = "$LICENSES_FILE" ] && return 0
            if [ -n "$licenses_text" ] && printf '%s\n' "$licenses_text" | grep -qF -- "\`$rel\`"; then
                return 0
            fi
            ;;
    esac
    return 1
}

violations=$(printf '%s\n' "$files" | while IFS= read -r f; do
    [ -z "$f" ] && continue
    kind=$(extension_kind "$f")
    [ -z "$kind" ] && kind=$(signature_kind "$f")
    [ -z "$kind" ] && continue
    if ! is_allowed "$f"; then
        printf '  %s  (%s)\n' "$f" "$kind"
    fi
done)

if [ -n "$violations" ]; then
    echo "check-images: images/RAW files are not allowed in this public repository:" >&2
    printf '%s\n' "$violations" >&2
    echo "" >&2
    echo "Only files under ${PUBLIC_DIR} listed in ${LICENSES_FILE} (with a verified" >&2
    echo "free license) or exact paths in ${ALLOW_FILE} may be committed." >&2
    exit 1
fi

exit 0
