#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD_DIR="$SCRIPT_DIR/build"
SRC_DIR="$SCRIPT_DIR/introdb-plugin/IntroDbPlugin"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

print_status() {
    echo -e "${GREEN}[BUILD]${NC} $1"
}

print_warning() {
    echo -e "${YELLOW}[WARN]${NC} $1"
}

print_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

# Clean build directory
clean_build() {
    print_status "Cleaning build directory..."
    rm -rf "$BUILD_DIR"
    mkdir -p "$BUILD_DIR"
}

# Plugin version, overridable via VERSION env var (e.g. VERSION=1.2.0.0 ./build.sh)
VERSION="${VERSION:-1.0.0.0}"

# Check if a given .NET SDK major version is available
check_sdk_available() {
    dotnet --list-sdks 2>/dev/null | grep -q "^$1\."
}

# Required .NET SDK major version for a Jellyfin version
required_sdk() {
    case "$1" in
        10.10) echo 8 ;;
        10.11) echo 9 ;;
        12.2) echo 10 ;;
    esac
}

# Zip a build output directory and write its md5 checksum
package_output() {
    local output_dir=$1
    local name=$2
    local zip_file="$BUILD_DIR/introdb_${VERSION}_${name}.zip"

    (cd "$output_dir" && zip -q -r "$zip_file" .)
    (cd "$BUILD_DIR" && md5sum "$(basename "$zip_file")" 2>/dev/null || md5 -r "$(basename "$zip_file")") \
        | awk '{print $1 "  " $2}' > "${zip_file%.zip}.md5"
    print_status "Packaged $zip_file"
}

# Build Jellyfin version
build_jellyfin() {
    local version=$1
    local output_dir="$BUILD_DIR/jellyfin-$version"

    local sdk
    sdk=$(required_sdk "$version")
    if ! check_sdk_available "$sdk"; then
        print_warning "Jellyfin $version requires .NET $sdk SDK which is not installed."
        print_warning "Skipping Jellyfin $version build. Install .NET $sdk SDK to build this version."
        return 0
    fi

    print_status "Building for Jellyfin $version..."

    dotnet build "$SRC_DIR/IntroDbPlugin.csproj" \
        -c Release \
        -p:JellyfinVersion="$version" \
        -p:Version="$VERSION" \
        -o "$output_dir" \
        --nologo

    # Clean up unnecessary files
    rm -f "$output_dir"/*.deps.json
    rm -f "$output_dir"/*.pdb

    # Write meta.json with the targetAbi and version for this build
    sed -E \
        -e "s/\"targetAbi\": \"[^\"]*\"/\"targetAbi\": \"$version.0.0\"/" \
        -e "s/\"version\": \"[^\"]*\"/\"version\": \"$VERSION\"/" \
        "$SRC_DIR/meta.json" > "$output_dir/meta.json"

    package_output "$output_dir" "jellyfin-$version"
    print_status "Jellyfin $version build complete: $output_dir"
}

# Build Emby version
build_emby() {
    local output_dir="$BUILD_DIR/emby"

    print_status "Building for Emby..."

    dotnet build "$SRC_DIR/IntroDbPlugin.Emby.csproj" \
        -c Release \
        -p:Version="$VERSION" \
        -o "$output_dir" \
        --nologo

    # Clean up unnecessary files
    rm -f "$output_dir"/*.deps.json
    rm -f "$output_dir"/*.pdb

    package_output "$output_dir" "emby"
    print_status "Emby build complete: $output_dir"
}

# Show usage
usage() {
    echo "Usage: $0 [OPTIONS]"
    echo ""
    echo "Options:"
    echo "  --all           Build all versions (default)"
    echo "  --jellyfin-10.10  Build for Jellyfin 10.10 only"
    echo "  --jellyfin-10.11  Build for Jellyfin 10.11 only"
    echo "  --jellyfin-12.2   Build for Jellyfin 12.2 only"
    echo "  --emby          Build for Emby only"
    echo "  --clean         Clean build directory only"
    echo "  --help          Show this help message"
    echo ""
    echo "Examples:"
    echo "  $0              # Build all versions"
    echo "  $0 --jellyfin-12.2  # Build only Jellyfin 12.2"
    echo "  VERSION=1.2.0.0 $0  # Build all versions stamped as 1.2.0.0"
    echo "  $0 --emby       # Build only Emby"
}

# Main
main() {
    local build_jellyfin_10_10=false
    local build_jellyfin_10_11=false
    local build_jellyfin_12_2=false
    local build_emby_flag=false
    local clean_only=false

    # Parse arguments
    if [ $# -eq 0 ]; then
        build_jellyfin_10_10=true
        build_jellyfin_10_11=true
        build_jellyfin_12_2=true
        build_emby_flag=true
    else
        while [ $# -gt 0 ]; do
            case "$1" in
                --all)
                    build_jellyfin_10_10=true
                    build_jellyfin_10_11=true
                    build_jellyfin_12_2=true
                    build_emby_flag=true
                    ;;
                --jellyfin-10.10)
                    build_jellyfin_10_10=true
                    ;;
                --jellyfin-10.11)
                    build_jellyfin_10_11=true
                    ;;
                --jellyfin-12.2)
                    build_jellyfin_12_2=true
                    ;;
                --emby)
                    build_emby_flag=true
                    ;;
                --clean)
                    clean_only=true
                    ;;
                --help|-h)
                    usage
                    exit 0
                    ;;
                *)
                    print_error "Unknown option: $1"
                    usage
                    exit 1
                    ;;
            esac
            shift
        done
    fi

    # Always clean first
    clean_build

    if [ "$clean_only" = true ]; then
        print_status "Clean complete."
        exit 0
    fi

    # Build requested versions
    if [ "$build_jellyfin_10_10" = true ]; then
        build_jellyfin "10.10"
    fi

    if [ "$build_jellyfin_10_11" = true ]; then
        build_jellyfin "10.11"
    fi

    if [ "$build_jellyfin_12_2" = true ]; then
        build_jellyfin "12.2"
    fi

    if [ "$build_emby_flag" = true ]; then
        build_emby
    fi

    echo ""
    print_status "Build complete!"
    echo ""
    echo "Build outputs:"
    ls -la "$BUILD_DIR"
}

main "$@"
