# Library exclusions

Right-click a media tile and choose **Exclude this folder…**, **Exclude this source…**, or **Exclude this file type…**. Read the scope in the confirmation before choosing **Exclude**. For a file at the source root, excluding its folder means excluding the entire source, which the confirmation states explicitly.

- Folder rules include descendants, not similarly named sibling folders. They belong to the selected registered source; overlapping sources are independent.
- Source rules hide that entire registered source, including its folders.
- File-type rules match an exact extension, case-insensitively, across every source. `.jpg` and `.jpeg` are separate rules. These are extension rules, not file-content detection.

Matching media disappears from the gallery, search, timeline/month counts, favorites, album counts and preview-preparation totals. Future scans skip excluded folders without traversing them and skip excluded file types. Entire excluded sources are not scanned. Source registration remains available in Manage sources.

## Review, add or undo rules

Open **Settings → Manage exclusions…** or **Refresh → Manage exclusions…**, including when the gallery is empty. Add an extension before importing a source, exclude an existing registered source, or choose a folder inside a registered source. Folder selection uses the most specific registered root. Use separate rules for separately registered overlapping roots.

Choose **Restore** beside a rule to remove it. Already cataloged media reappears immediately with favorites, tags and album memberships intact, unless another rule still excludes it. Refresh the source to discover files skipped while excluded or reconcile originals changed outside Luma. Restoring a rule does not automatically scan your drive.

Exclusions persist across restart and reconnect under the source's stable identity. **Reset** only clears browsing filters; it does not remove exclusion rules. File-type rules remain global when sources are removed. Removing a source clears its source/folder rules along with that source's catalog and organization; use exclusion for reversible hiding instead.

## Work in progress and storage

Pause scanning or preview work in **Activity** before changing rules. Changing source/folder rules restarts saved traversal checkpoints for that source; changing a global file-type rule restarts all saved traversals. Published catalog data is preserved. This prevents an old checkpoint from republishing excluded files.

Originals are never moved or deleted by exclusions. Existing catalog records and previews are retained for restoration. Unused-preview cleanup does not consider those retained previews orphaned; normal cache limits and explicit cache clearing still apply. New preview preparation and metadata reading skip excluded media.

Older builds do not understand exclusions and may show hidden media. Upgrade using the complete new portable ZIP; preserve a closed-app data backup before rollback.
