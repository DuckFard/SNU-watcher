# Legacy watcher migration

This describes the optional migration implementation for the earlier Python watcher. The student-facing import screen is a later UI milestone. PDF import is outside the app's scope.

## Explicit selection and preview

The reader accepts explicitly selected local TOML configuration and/or SQLite state files. It does not discover personal files, start or stop the Python watcher, follow the configuration's database path, or resolve webhook environment variables. Watcher settings and delivery acknowledgement are not imported.

Configuration is limited to 256 KiB, 20 course entries and 16 nesting levels. State input is limited to 25 MiB including its WAL and 2,000 rows. Unsupported files or malformed rows produce fixed diagnostic codes and positions; raw parser messages and file contents are not included in diagnostics. Parsing runs away from the UI thread. Cancellation is cooperative between bounded steps: the TOML parser itself is not interruptible and SQLite lock waits have a five-second timeout. Diagnostics are capped at 2,000 entries with a truncation marker.

The SQLite reader recognizes the original unversioned twelve-column `course_state` schema. It opens a read-only connection and reads one transactionally consistent snapshot, including committed live WAL data. A WAL-format source missing its WAL or shared-memory sidecar is rejected instead of creating or repairing those files. It never invokes the old repository constructor, copies only the live main database, or marks an active database immutable. SQLite may use an existing shared-memory read mark; byte-for-byte preservation of that synchronization file is not claimed.

Configuration and state rows merge by their original four-part identity: year, composite semester code, course code and three-digit section. A state-only row is marked as historical rather than presumed to be an actively configured watch. Punctuation and leading zeros remain strings throughout.

The merged preview also has a 2,000-row limit. If the combined sources exceed it, the reader retains the configuration contribution, rejects the state contribution and reports that limit explicitly.

## Confirming the target

An old identity does not contain the new catalog's separate semester, detail-semester and subsubject fields. The caller must search the catalog and present full candidate identities for explicit student selection. The confirmation model rejects a target absent from those candidates or a row absent from the preview. It never silently chooses the current term or guesses a unique match from a score. The pure model cannot attest that caller-supplied candidates actually came from SNU; the eventual application search flow owns that boundary.

Each confirmed item creates an individual section watch with the student's selected preferences. Historical name, counts and original observation time are provenance only. They do not become current availability, initialize a subscription generation, create an opening event or acknowledge a notification. The new watcher must obtain fresh source data before deciding availability.

## Atomic import and repeat imports

All selected items are imported in one transaction. The ledger key combines the original legacy identity with the confirmed full target identity; it excludes file paths, changing counts, labels and batch identifiers. Reimporting the same mapping returns its existing watch, including its current preferences, paused status or removal tombstone. It does not recreate a removed watch or reset initialization. A failed import rolls back both newly created watches and ledger entries.

## Database upgrades and recovery

The application store recognizes its own application ID, schema version and known producer schema before changing journal mode or schema. Foreign, altered and newer-version databases are rejected instead of repaired. Version two adds the legacy-import ledger; version three adds durable delivery batches/attempts and ordered source-request policy. Version four adds revision-checked UI preferences, successful-save receipts and a small nonauthoritative term-label cache. Version five adds the ordered alert-history index. Fresh stores create the current schema directly. A recognized version-one, version-two, version-three or version-four store upgrades directly to version five in one transaction, without committing an intermediate version. Earlier producer DDL remains unchanged.

Before modifying an existing recognized store, the upgrade creates one separate, consistent native SQLite backup next to it with a unique `.before-v5-<id>.sqlite3` suffix. The backup includes committed WAL content and is verified as a standalone database of its original version before upgrade DDL begins. Version-two backups also preserve the import ledger; version-three backups additionally preserve batch attempts and source policy; version-four backups preserve preferences and their original accepted receipts. Newly saved preference values include sound muting, while old values default to unmuted and existing receipts retain their original bytes. Backup work has cancellation, time and page bounds. Completed backups are preserved; handled failures clean up incomplete owned artifacts. A process crash can leave an incomplete artifact, which is never automatically restored. Migration DDL and version changes share a transaction, so interruption leaves the original version usable and a later open can retry.

Backups contain local watch data and receive private file permissions. They are not uploaded or automatically restored. If an upgrade fails, keep the original database and completed backup, close the app, and collect a sanitized diagnostic before attempting recovery. Do not overwrite a newer live database with an old backup while another owner is running. Retention and the eventual recovery UI belong to release hardening.

## Verification boundary

Integration fixtures exercise live WAL snapshots, source preservation, duplicate imports, tombstones, transaction faults and interrupted schema upgrades. These use generated test data; the user's existing Python runtime, configuration, state and processes remain separate. Final measured test results are recorded in [progress](PROGRESS.md) after the integrated checks complete.
