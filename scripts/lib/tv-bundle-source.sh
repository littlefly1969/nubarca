#!/usr/bin/env bash
# When a TV bundle built from an earlier commit is still the bundle of a later one.
#
# CI publishes TV bundles (native APK, signed OTA) only when someone runs the
# TV release workflows, tagged with the exact commit they were built from. A
# server release usually does not touch the TV at all, so requiring a bundle
# for every candidate commit made each such release wait on a manual TV run
# that would rebuild the same bytes.
#
# A bundle built from <source> is the bundle of <target> exactly when <source>
# is an ancestor of <target> and none of the TV bundle's build inputs changed
# between them. That is decided here, from Git, and nowhere else: the updater
# uses it to find a reusable bundle, and both importers use it to accept one.
#
#   . "$REPO_ROOT/scripts/lib/tv-bundle-source.sh"
#   tv_bundle_reusable "$source_sha" "$target_sha" || fail "..."

# Everything a TV bundle is built from: the TV application and the two
# workflows that build, sign and package it.
TV_BUNDLE_PATHS=(
    tv
    .github/workflows/tv-native-release.yml
    .github/workflows/tv-ota-release.yml
)

# How far back the updater looks for a reusable bundle, in first-parent commits.
TV_BUNDLE_SEARCH_LIMIT=50

tv_bundle_reusable() {
    local source="$1" target="$2"
    [[ "$source" =~ ^[0-9a-f]{40}$ && "$target" =~ ^[0-9a-f]{40}$ ]] || return 1
    [[ "$source" == "$target" ]] && return 0
    git cat-file -e "$source^{commit}" 2>/dev/null || return 1
    git merge-base --is-ancestor "$source" "$target" || return 1
    git diff --quiet "$source" "$target" -- "${TV_BUNDLE_PATHS[@]}"
}

# Print the commits whose bundle would be <target>'s bundle, newest first:
# <target> itself, then its first-parent ancestors back to the last commit that
# changed a TV build input (inclusive), at most TV_BUNDLE_SEARCH_LIMIT of them.
tv_bundle_candidate_sources() {
    local target="$1" sha
    while read -r sha; do
        tv_bundle_reusable "$sha" "$target" || break
        printf '%s\n' "$sha"
    done < <(git rev-list --first-parent --max-count="$TV_BUNDLE_SEARCH_LIMIT" "$target")
}
