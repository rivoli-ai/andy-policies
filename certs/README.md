# Certificates

Place your corporate CA certificates (`.crt`, `.pem`, `.cer`) in this directory.

They will be automatically trusted during Docker builds and at container runtime.

**Do NOT commit private keys or sensitive certificates to this directory.**

For development, the Docker image generates a self-signed certificate automatically.
