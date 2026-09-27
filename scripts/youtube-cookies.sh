#!/usr/bin/env bash
# Keeps a YouTube cookies file fresh for YouTubeSync, from a dedicated Firefox profile.
#
#   youtube-cookies.sh login     One-time: open Firefox with the dedicated profile so you can sign in to YouTube.
#                                Needs a desktop session (run it on the machine's desktop, not over plain SSH).
#   youtube-cookies.sh refresh   Keep the session alive, export the cookies, check them, and replace the cookies
#                                file only if the new cookies work. This is what the schedule runs.
#   youtube-cookies.sh install   Add a cron entry that runs "refresh" every 6 hours (and prints the plugin setting).
#   youtube-cookies.sh status    Show the profile, the cookies file and the last log lines.
#
# Why a browser profile instead of a one-off cookies.txt export: YouTube rotates its session cookies from inside
# an open browser, so an exported file slowly goes stale and eventually stops working. Here a headless Firefox
# visits YouTube on every refresh, which lets the session rotate normally, and the fresh cookies are exported.
#
# Settings (environment variables, all optional):
#   YTC_DIR           Base folder (default: the folder this script is in)
#   YTC_PROFILE       Firefox profile folder (default: $YTC_DIR/firefox-youtube)
#   YTC_COOKIES       Cookies file the plugin reads (default: $YTC_DIR/youtube-cookies.txt)
#   YTC_YTDLP         yt-dlp binary (default: yt-dlp on PATH)
#   YTC_YTDLP_ARGS    Extra yt-dlp arguments, e.g. "--plugin-dirs /path/to/yt-dlp-plugins"
#   YTC_FIREFOX       Firefox binary (default: firefox)
#   YTC_GROUP         Group that may read the cookies file (default: your primary group); the Jellyfin
#                     service user must be in it
#   YTC_TEST_VIDEO    Optional age-restricted video ID; when set, refresh also reports whether it plays
#   YTC_SCHEDULE      Cron schedule for "install" (default: "17 */6 * * *")
set -euo pipefail
# cron runs with a minimal PATH; yt-dlp and deno (its YouTube challenge solver) often live in /usr/local/bin.
export PATH="/usr/local/bin:/usr/bin:/bin:$PATH"

SCRIPT_PATH="$(readlink -f "${BASH_SOURCE[0]}")"
YTC_DIR="${YTC_DIR:-$(dirname "$SCRIPT_PATH")}"
YTC_PROFILE="${YTC_PROFILE:-$YTC_DIR/firefox-youtube}"
YTC_COOKIES="${YTC_COOKIES:-$YTC_DIR/youtube-cookies.txt}"
YTC_YTDLP="${YTC_YTDLP:-yt-dlp}"
YTC_YTDLP_ARGS="${YTC_YTDLP_ARGS:-}"
YTC_FIREFOX="${YTC_FIREFOX:-firefox}"
YTC_GROUP="${YTC_GROUP:-$(id -gn)}"
YTC_TEST_VIDEO="${YTC_TEST_VIDEO:-}"
YTC_SCHEDULE="${YTC_SCHEDULE:-17 */6 * * *}"
LOG_FILE="$YTC_DIR/youtube-cookies.log"

log() { printf '%s %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*" | tee -a "$LOG_FILE" >&2; }
die() { log "ERROR: $*"; exit 1; }

# Firefox leaves a "lock" symlink while the profile is open; a second instance on the same profile fails.
profile_in_use() {
    [ -L "$YTC_PROFILE/lock" ] || return 1
    # The link target is "<ip>:+<pid>".
    local pid
    pid="$(readlink "$YTC_PROFILE/lock" | sed -n 's/.*+\([0-9][0-9]*\)$/\1/p')"
    [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null
}

run_ytdlp() {
    # shellcheck disable=SC2086 # YTC_YTDLP_ARGS is intentionally word-split
    "$YTC_YTDLP" --ignore-config --no-warnings $YTC_YTDLP_ARGS "$@"
}

cmd_login() {
    mkdir -p "$YTC_PROFILE"
    [ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ] || die "No desktop session. Run 'login' from the machine's desktop (a terminal there), not over plain SSH."
    profile_in_use && die "Firefox is already open with this profile. Use that window, or close it first."
    log "Opening Firefox with the YouTube profile. Sign in to YouTube, check a video plays, then close Firefox."
    "$YTC_FIREFOX" --no-remote --profile "$YTC_PROFILE" "https://accounts.google.com/ServiceLogin?service=youtube&continue=https://www.youtube.com/" || true
    log "Firefox closed. Running a refresh to export the cookies."
    cmd_refresh
}

# Let the session rotate by loading YouTube in a headless Firefox for a short while.
touch_session() {
    if profile_in_use; then
        log "Firefox is open with the profile; skipping the headless visit (the open window keeps the session fresh)."
        return
    fi
    local pid
    MOZ_HEADLESS=1 "$YTC_FIREFOX" --headless --no-remote --profile "$YTC_PROFILE" "https://www.youtube.com/" >/dev/null 2>&1 &
    pid=$!
    sleep 45
    kill "$pid" 2>/dev/null || true
    # Give Firefox a moment to flush cookies.sqlite to disk, then make sure it's gone.
    for _ in 1 2 3 4 5 6 7 8 9 10; do kill -0 "$pid" 2>/dev/null || break; sleep 1; done
    kill -9 "$pid" 2>/dev/null || true
    rm -f "$YTC_PROFILE/lock" "$YTC_PROFILE/.parentlock" 2>/dev/null || true
}

cmd_refresh() {
    [ -f "$YTC_PROFILE/cookies.sqlite" ] || die "No Firefox profile at $YTC_PROFILE yet. Run '$SCRIPT_PATH login' on the desktop first."
    command -v "$YTC_YTDLP" >/dev/null || [ -x "$YTC_YTDLP" ] || die "yt-dlp not found ($YTC_YTDLP)."

    touch_session

    tmp="$(mktemp "$YTC_DIR/.youtube-cookies.XXXXXX")"
    check="$(mktemp "$YTC_DIR/.youtube-cookies-check.XXXXXX")"
    trap 'rm -f "$tmp" "$tmp.filtered" "$check"' EXIT
    # yt-dlp also reads the --cookies file first, and rejects an empty one.
    echo "# Netscape HTTP Cookie File" > "$tmp"

    # --cookies-from-browser loads the profile's cookies; --cookies FILE dumps them to FILE on exit.
    # ":ytfav" (liked videos) only works when signed in, so it doubles as the sign-in check.
    if ! run_ytdlp --cookies-from-browser "firefox:$YTC_PROFILE" --cookies "$tmp" \
            --flat-playlist --playlist-items 1 --print id ":ytfav" >/dev/null 2>"$check"; then
        die "Export failed; keeping the current cookies file. yt-dlp said: $(tail -n 3 "$check" | tr '\n' ' ')"
    fi

    # Keep only YouTube/Google cookies, and make sure the sign-in cookies are there.
    local filtered="$tmp.filtered"
    { echo "# Netscape HTTP Cookie File"; grep -E '^(#HttpOnly_)?\.?(youtube\.com|google\.com)[[:space:]]' "$tmp" || true; } > "$filtered"
    mv "$filtered" "$tmp"
    grep -qE '[[:space:]](LOGIN_INFO|__Secure-3PSID)[[:space:]]' "$tmp" \
        || die "The profile isn't signed in to YouTube (no LOGIN_INFO / __Secure-3PSID cookie). Run '$SCRIPT_PATH login' on the desktop."

    # Test the exported file on its own (on a copy, since yt-dlp rewrites its --cookies file).
    cp "$tmp" "$check"
    if ! run_ytdlp --cookies "$check" --flat-playlist --playlist-items 1 --print id ":ytfav" >/dev/null 2>&1; then
        die "The exported cookies don't work on their own; keeping the current cookies file."
    fi

    if [ -n "$YTC_TEST_VIDEO" ]; then
        cp "$tmp" "$check"
        if run_ytdlp --cookies "$check" --simulate --print id "https://www.youtube.com/watch?v=$YTC_TEST_VIDEO" >/dev/null 2>&1; then
            log "Age-restricted test video $YTC_TEST_VIDEO plays with these cookies."
        else
            log "WARNING: age-restricted test video $YTC_TEST_VIDEO still doesn't play; the account may not be age-verified."
        fi
    fi

    # Readable by the Jellyfin service user through the group, not by everyone; replaced atomically.
    chgrp "$YTC_GROUP" "$tmp" 2>/dev/null || true
    chmod 640 "$tmp"
    mv -f "$tmp" "$YTC_COOKIES"
    log "Cookies refreshed: $YTC_COOKIES ($(grep -vc '^#' "$YTC_COOKIES") cookies)."
    tail -n 500 "$LOG_FILE" > "$LOG_FILE.tmp" 2>/dev/null && mv -f "$LOG_FILE.tmp" "$LOG_FILE" || true
}

cmd_install() {
    local env_line="" var
    for var in YTC_PROFILE YTC_COOKIES YTC_YTDLP YTC_YTDLP_ARGS YTC_FIREFOX YTC_GROUP YTC_TEST_VIDEO; do
        [ -n "${!var:-}" ] && env_line+="$var=$(printf '%q' "${!var}") "
    done
    local entry="$YTC_SCHEDULE ${env_line}$(printf '%q' "$SCRIPT_PATH") refresh >/dev/null 2>&1 # youtubesync-cookies"
    { crontab -l 2>/dev/null | grep -v '# youtubesync-cookies$' || true; echo "$entry"; } | crontab -
    log "Scheduled: $entry"
    echo
    echo "In Jellyfin, set YouTube Sync > YouTube cookies file to:"
    echo "  $YTC_COOKIES"
}

cmd_status() {
    echo "Profile:  $YTC_PROFILE $( [ -f "$YTC_PROFILE/cookies.sqlite" ] && echo '(exists)' || echo '(not set up; run login)')"
    if [ -f "$YTC_COOKIES" ]; then
        echo "Cookies:  $YTC_COOKIES (updated $(date -r "$YTC_COOKIES" '+%Y-%m-%d %H:%M'))"
    else
        echo "Cookies:  $YTC_COOKIES (missing)"
    fi
    echo "Schedule: $(crontab -l 2>/dev/null | grep '# youtubesync-cookies$' || echo 'not installed')"
    [ -f "$LOG_FILE" ] && { echo; tail -n 10 "$LOG_FILE"; }
}

case "${1:-refresh}" in
    login) cmd_login ;;
    refresh) cmd_refresh ;;
    install) cmd_install ;;
    status) cmd_status ;;
    *) echo "Usage: $0 {login|refresh|install|status}" >&2; exit 2 ;;
esac
