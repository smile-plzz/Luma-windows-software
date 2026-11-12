# Final experience: v0.6.0

## Pick up where you left off

Luma remembers the current source, relative folder, album, search/category, selected month or memory day, and first visible media item with its scroll inset. Existing sort and display preferences still apply. Position saves after a short idle period and on normal close; a forced termination may lose the latest second of movement.

Reopening locates that item in the local catalog and loads a small window around it. It does not scan the drive or load every preceding thumbnail. **Earlier items** loads the preceding 120 records while preserving position; **Back to start** returns to the beginning of the same view. **Reset** clears browsing filters and returns to the full library; exclusions remain active. Layout changes, resizing and collection changes may adjust the exact pixel position.

Restoration also works with disconnected drives and cached previews. Removed sources fall back to the library; missing/excluded anchors fall back to the first available items in the saved view. Empty views keep clear recovery guidance. File selection and pending clipboard moves are deliberately not restored. Settings → **Remember where I left off** disables restoration and clears the saved place.

## Long imports

- **Step 1 of 2, Finding media:** files examined this pass, media records saved, folders completed, inaccessible folders, current source/folder, and elapsed time. Discovery has no percentage because the total is not yet known. Completed folder counts include reused checkpoints.
- **Step 2 of 2, Reading dates:** known pending total, checked files, committed metadata updates and skipped unavailable/changed files. The progress bar reflects checked files, not a claim that every file has a capture date. Cached, unchanged metadata is not read again.

Hover over the status or open **Activity** for the full path and details. Activity reports how long no new update has arrived; a slow drive or codec call can delay cancellation. **Pause** requests a safe stop. Discovery resumes through Activity; date reading resumes through Manage sources → More → Refresh metadata. Completed metadata batches are retained. The saved gallery remains browsable during work.

Metadata totals are per source. Files modified externally during import can be skipped and retried by refreshing; there is no estimated completion time or drive-speed promise. Preview maintenance remains a separate operation under Storage & previews.

## Finishing polish

Main browsing, filtering, file-operation, exclusion and maintenance controls explain their effects through tooltips and accessible help. Favorites, tags, settings and pause requests acknowledge the result. Existing destructive confirmations remain. Dialogs follow the gallery's light/dark appearance, and notices announce feedback politely to assistive technology.

This closes the planned product build. Further development is limited to reported defects and real-use feedback; signing, installers and unrequested features remain future decisions.
