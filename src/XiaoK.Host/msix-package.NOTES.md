# MSIX packaging gate

Package.appxmanifest declares the Windows notification-listener capability and current-user startup task. This file is a packaging input only: the WPF project is not yet built as a signed MSIX, no signing certificate is configured, and Windows notification access has not been requested or granted. The notification listener must be registered only after a signed package is installed and the user explicitly grants access.

The package publisher, logos, package architecture, app identity, startup-task registration, upgrade and rollback behavior must be finalized before P4 release. Do not install this draft manifest or use an unsigned identity to bypass Windows consent.
