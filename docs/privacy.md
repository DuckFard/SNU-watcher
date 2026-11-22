# Privacy and local data

SNU Watcher is an independent desktop companion. The preview does not ask for SNU credentials, import browser cookies, log in, submit registrations, or reserve seats. Registration and SNU Genie links open in your normal browser, where those services apply their own policies.

## Network use

The app sends anonymous course-catalog, search and detail requests to the official SNU registration service. Requests include selected academic terms, course identifiers and search filters. SNU and the network provider can observe these requests and the connection's IP address. A fresh anonymous HTTP session can keep service-issued cookies in memory; the app does not import an authenticated browser session or retain those cookies as a login.

The preview has no project-operated server, analytics or automatic crash reporting. It does not upload your watchlist or database. Manual links to GitHub Releases and SNU/Genie use your browser and those sites' normal network behavior. Automatic update checks are deferred in this preview.

## Files on your computer

Watches, section identities, historical availability, delivery records and preferences are stored in SQLite under `%LOCALAPPDATA%\DuckFard\SnuWatcher`. Schema upgrades can create a verified local backup beside the database. These files contain personal course choices. Protect them as you would your timetable; they are not encrypted by the app and are protected by normal Windows user permissions.

Uninstall preserves local data by default. Quit the app, including its tray owner, before making an offline backup or managing its files. Do not remove a live SQLite WAL or shared-memory file. Do not overwrite a newer live database with an older backup to downgrade an app.

The preview does not automatically prune its historical records. Storage reporting warns at 256 MiB; that warning is an application policy, not a hard storage cap. Pause watches or quit if you want to stop collecting history. Semantic retention and a data-management interface are deferred.

## Notifications and diagnostics

Windows notifications can display course identifiers, counts and observation times on the desktop, lock screen or notification center according to your Windows settings. Windows acceptance does not establish that a banner was shown. A notification's direct browser action does not give the app a reliable click receipt. Seen/NotSeen answers for test notifications record only your own answer about that test.

Diagnostic export is opt-in and local. Its bounded summary is limited to app/runtime versions, fixed health/readiness/preferences facts and storage byte sizes. It excludes database contents, course identifiers, watch names, account names, file paths, cookies, raw server responses and exception text. Review any file before sharing it. No export is uploaded automatically.

Development validation profiles and screenshots contain explicitly marked fixture data and remain separate from the normal profile. Maintainers must not attach personal databases, credentials, real watch exports or unreviewed logs to GitHub issues or releases.
