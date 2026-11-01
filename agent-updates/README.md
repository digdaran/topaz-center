# agent-updates

CI fills this folder with signed agent packages (`<version>/<rid>/{manifest.json, manifest.sig, *.zip}`) before building the server image, so agents can update themselves from their own server. In a plain local `docker build` it stays empty and no updates are offered.
