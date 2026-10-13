# Contributing to Immich Desktop

Thanks for your interest in improving Immich Desktop! This is an unofficial, community-built desktop backup client for [Immich](https://immich.app). It is not affiliated with or endorsed by the Immich project or FUTO.

By participating you agree to follow our [Code of Conduct](CODE_OF_CONDUCT.md). By contributing you agree your work is licensed under the project's [AGPL-3.0 license](LICENSE).

## Ways to help

- Report bugs and request features through [issues](../../issues/new/choose).
- - Test the beta channel and share feedback.
  - - Improve documentation and screenshots.
    - - Help with Linux support, which is in progress.
     
      - ## Before you open a pull request
     
      - - Keep each PR focused on a single change. Smaller PRs are reviewed faster.
        - - For large or impactful changes, open an issue to discuss the approach first.
          - - Fill out the whole pull request template.
            - - Build and run the tests (`dotnet test`) and make sure they pass.
              - - Follow the existing code style and structure.
               
                - ## AI disclosure and AI-assisted contributions
               
                - This project was built with substantial help from AI coding assistants (Claude, by Anthropic), directed and reviewed by the maintainer. Commits made with AI assistance are marked with co-author trailers.
               
                - AI-assisted contributions are welcome here, with a few expectations: disclose in your PR that AI tools were used, review and test everything yourself, and be able to explain every change you submit. Low-effort, unreviewed, or bulk automated PRs will be closed.
               
                - Note that the upstream Immich project has its own, stricter policy and does not accept LLM-generated pull requests. Do not submit code from this project to Immich without reading [their contribution policy](https://github.com/immich-app/immich/blob/main/CONTRIBUTING.md).
               
                - ## Machine learning and third-party assets
               
                - Immich Desktop does not bundle or run any machine learning models. Smart search, face recognition, and similar features are performed by your Immich server, not by this app. If you propose adding a model, library, icon, or other third-party asset, state its license in the PR and make sure it is compatible with AGPL-3.0 and, for models, that its terms allow redistribution.
               
                - ## Branding
               
                - Immich is a trademark of its respective owners. Please do not use the Immich logo in a way that suggests this app is official.
               
                - ## Security
               
                - Do not report vulnerabilities in public issues. See [SECURITY.md](SECURITY.md).
                - 
