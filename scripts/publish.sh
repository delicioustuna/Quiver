#!/usr/bin/env bash
set -euo pipefail

EXCLUDE_PATHS=(
    "plans/"
    "sandbox/"
    "docs/design/"
    "docs/generated/"
    "benchmarks/baselines/README.md"
)

SANDBOX_SLNX_PATTERNS=(
    'sandbox\\QuiverSandbox\\QuiverSandbox.csproj'
    'sandbox\\RagSandbox\\RagSandbox.csproj'
)

SOURCE_BRANCH="develop"
TARGET_BRANCH="main"

DRY_RUN=false
if [[ "${1:-}" == "--dry-run" ]]; then
    DRY_RUN=true
fi

log() { echo "==> $*"; }
die() { echo "ERROR: $*" >&2; exit 1; }

current_branch=$(git symbolic-ref --short HEAD)
if [[ "$current_branch" != "$SOURCE_BRANCH" ]]; then
    die "Current branch is '$current_branch', expected '$SOURCE_BRANCH'. Aborting."
fi

if $DRY_RUN; then
    log "[DRY RUN] Previewing what would change on '$TARGET_BRANCH'"
    echo ""

    log "Files that would be excluded from '$TARGET_BRANCH':"
    for path in "${EXCLUDE_PATHS[@]}"; do
        git ls-tree -r --name-only "$SOURCE_BRANCH" -- "$path" 2>/dev/null | sed 's/^/  /'
    done
    echo ""

    log "Diff from '$TARGET_BRANCH' to '$SOURCE_BRANCH' (excluding above):"
    diff_args=()
    for path in "${EXCLUDE_PATHS[@]}"; do
        diff_args+=(":(exclude)$path")
    done
    git diff "$TARGET_BRANCH"..."$SOURCE_BRANCH" --stat -- . "${diff_args[@]}" || true
    echo ""

    log "slnx sandbox entries that would be removed:"
    for pat in "${SANDBOX_SLNX_PATTERNS[@]}"; do
        echo "  $pat"
    done

    echo ""
    log "[DRY RUN] No changes were made."
    exit 0
fi

if [[ -n "$(git status --porcelain)" ]]; then
    die "Working tree is dirty. Commit or stash changes first."
fi

log "Checking out '$TARGET_BRANCH'..."
git checkout "$TARGET_BRANCH"

log "Merging '$SOURCE_BRANCH' into '$TARGET_BRANCH' (--no-commit)..."
if ! git merge "$SOURCE_BRANCH" --no-ff --no-commit --no-edit; then
    log "Merge conflicts detected. Checking if all are in excluded paths..."

    conflict_files=$(git diff --name-only --diff-filter=U)
    has_real_conflict=false
    for cf in $conflict_files; do
        in_exclude=false
        for path in "${EXCLUDE_PATHS[@]}"; do
            if [[ "$cf" == "$path"* ]]; then
                in_exclude=true
                break
            fi
        done
        if ! $in_exclude; then
            has_real_conflict=true
            echo "  CONFLICT (not excluded): $cf"
        fi
    done

    if $has_real_conflict; then
        echo ""
        die "Merge conflict in non-excluded files. Resolve manually, then re-run.
  To abort: git merge --abort && git checkout $SOURCE_BRANCH"
    fi

    log "All conflicts are in excluded paths — auto-resolving by deletion."
    for cf in $conflict_files; do
        git rm -f "$cf" &>/dev/null || true
    done
fi

log "Removing excluded paths from index..."
for path in "${EXCLUDE_PATHS[@]}"; do
    if git ls-files --error-unmatch "$path" &>/dev/null; then
        git rm -rf --cached "$path"
        log "  Removed: $path"
    else
        log "  Skipped (not in index): $path"
    fi
done

log "Removing sandbox entries from Quiver.slnx..."
slnx="Quiver.slnx"
if [[ -f "$slnx" ]]; then
    cp "$slnx" "${slnx}.bak"

    sed -i '/<Folder Name="\/sandbox\/">/,/<\/Folder>/d' "$slnx"

    if diff -q "$slnx" "${slnx}.bak" &>/dev/null; then
        log "  No sandbox entries found in slnx (already clean)"
    else
        log "  Sandbox folder removed from slnx"
    fi
    rm -f "${slnx}.bak"
    git add "$slnx"
fi

log "Committing to '$TARGET_BRANCH'..."
git commit -m "Publish: sync from $SOURCE_BRANCH"

log "Switching back to '$SOURCE_BRANCH'..."
git checkout -f "$SOURCE_BRANCH"

echo ""
log "Done. '$TARGET_BRANCH' has been updated."
log "Review with: git log --oneline $TARGET_BRANCH -5"
log "Push with:   git push origin $TARGET_BRANCH"
