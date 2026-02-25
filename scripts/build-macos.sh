#!/usr/bin/env bash
set -euo pipefail

RID="${1:-osx-arm64}"
VERSION="${2:-dev}"

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT_PATH="$ROOT_DIR/InstallGWF/InstallGWF.csproj"
APP_NAME="InstallGWF"

PUBLISH_DIR="$ROOT_DIR/artifacts/publish/$RID"
DIST_DIR="$ROOT_DIR/artifacts/dist"
ARCHIVE_NAME="${APP_NAME}-${VERSION}-${RID}.tar.gz"
ARCHIVE_PATH="$DIST_DIR/$ARCHIVE_NAME"

mkdir -p "$PUBLISH_DIR" "$DIST_DIR"

dotnet restore "$PROJECT_PATH"
dotnet publish "$PROJECT_PATH" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$PUBLISH_DIR"

if [[ "$(uname -s)" == "Darwin" ]]; then
  brew_prefix="$(brew --prefix libmtp)"
  libmtp_dylib="$brew_prefix/lib/libmtp.dylib"

  if [[ ! -f "$libmtp_dylib" ]]; then
    echo "Expected libmtp at '$libmtp_dylib' but it was not found."
    exit 1
  fi

  bundle_dir="$PUBLISH_DIR"

  copy_and_patch_dylib() {
    local source_path="$1"
    local dep_name
    dep_name="$(basename "$source_path")"
    local local_path="$bundle_dir/$dep_name"

    if [[ ! -f "$local_path" ]]; then
      cp -L "$source_path" "$local_path"
      chmod u+w "$local_path" || true
    fi

    install_name_tool -id "@loader_path/$dep_name" "$local_path"

    while IFS= read -r dep; do
      [[ -z "$dep" ]] && continue
      local dep_base
      dep_base="$(basename "$dep")"
      local dep_local="$bundle_dir/$dep_base"

      if [[ ! -f "$dep_local" ]]; then
        copy_and_patch_dylib "$dep"
      fi

      install_name_tool -change "$dep" "@loader_path/$dep_base" "$local_path"
    done < <(otool -L "$local_path" | tail -n +2 | awk '{print $1}' | grep -E '^(/opt/homebrew|/usr/local)' || true)
  }

  copy_and_patch_dylib "$libmtp_dylib"

  # install_name_tool rewrites invalidate signatures; ad-hoc sign bundled Mach-O files.
  if command -v codesign >/dev/null 2>&1; then
    if [[ -f "$PUBLISH_DIR/$APP_NAME" ]]; then
      codesign --force --sign - --timestamp=none "$PUBLISH_DIR/$APP_NAME"
    fi

    while IFS= read -r dylib; do
      [[ -z "$dylib" ]] && continue
      codesign --force --sign - --timestamp=none "$dylib"
    done < <(find "$PUBLISH_DIR" -maxdepth 1 -type f -name "*.dylib" | sort)
  fi
fi

tar -czf "$ARCHIVE_PATH" -C "$PUBLISH_DIR" .
echo "Created $ARCHIVE_PATH"
