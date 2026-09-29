# Security policy

This project is a demonstration API, not a production service. Security fixes target the latest reviewed source on `main`.

## Responsible disclosure

Do not put credentials, real uploaded data, filesystem paths, or exploitable details in public issues or PRs. Use the repository's **Security -> Report a vulnerability** private channel when available. If it is unavailable, contact the maintainer privately through the contact method on [the maintainer profile](https://github.com/samuel-santos-engineer) to arrange disclosure before sharing details. If no private contact is available, open a public issue requesting a private channel without vulnerability details. Private vulnerability reporting is not assumed to be enabled by this milestone.

Include affected versions, a minimal reproduction with synthetic data, expected impact, and a proposed mitigation. Keep disclosure coordinated with the maintainer; no response-time guarantee is offered for this lab.

## Secrets and operation

Supply the upload API key through `Security__ApiKey` runtime environment/App Service configuration. Use a unique high-entropy production value (at least 32 random bytes); never commit it or include it in reports, logs, Swagger examples, or query strings. The development placeholder is public and explicitly restricted to local Development. Test markers are synthetic and must never be accepted as deployment credentials.

If a credential is exposed, revoke/rotate it first and privately coordinate removal from history. Passing secret scanning is useful evidence, not a guarantee that all secret types have been detected.

Read APIs and Swagger are intentionally public; use synthetic demonstration data. Enforce HTTPS at deployment ingress, restrict storage permissions, and protect persisted reports and backups. Rate limits use the connection peer and are per process; forwarded headers are not trusted. Deployment must explicitly review any trusted-proxy configuration. Docker/Azure validation remains deferred to the deployment milestone.
