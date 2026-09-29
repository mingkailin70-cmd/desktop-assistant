# Security policy

小K is intended to keep inference and private user data on the local Windows machine. The currently available source is an early engineering slice and is not ready for unattended message monitoring or sending.

## Reporting

Do not publish credentials, message contents, screenshots, model access tokens, or exploit details in public issues. For security-sensitive reports, contact the repository owner through GitHub's private vulnerability reporting feature when enabled. If it is unavailable, contact the owner privately before disclosure.

## Project safeguards

- The inference HTTP endpoint must be a loopback address.
- Tools are selected from a fixed registry; unknown actions are rejected.
- Message sending must show the final recipient, content, and attachments and require explicit confirmation.
- Notification bodies and model replies are transient by default; task state stores only a category and status.
- The draft MSIX manifest does not grant notification access by itself. The user must install a signed package and grant Windows permission.
