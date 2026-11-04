# Code signing (maintainer notes)

The Cube is prepared for free open-source code signing by [SignPath Foundation](https://signpath.org/).
Until the project is accepted, releases are unsigned and Windows SmartScreen warns about them.

## What's already in place

- **[release.yml](../.github/workflows/release.yml)** builds a draft GitHub release when a `v*` tag is pushed.
  It builds and tests on a GitHub-hosted runner, publishes `TheCube.exe` with the version from the tag,
  and uploads it as a workflow artifact (SignPath only signs workflow artifacts). If signing is set
  up, it sends that artifact to SignPath, waits for the signed file and puts it in the release.
  Otherwise the release is unsigned and its notes say so.
- **Signing switch:** the signing step runs only when the repository variable `SIGNPATH_ORGANIZATION_ID` is set.
- **Version metadata:** product name `The Cube` and product version = the tag, without a `+commit`
  suffix. SignPath Foundation requires both to be set and enforced.
- **[signpath-artifact-configuration.xml](signpath-artifact-configuration.xml):** a starting point for the
  artifact configuration in SignPath.
- **Policy:** the [Code signing policy](../README.md#code-signing-policy) section in the README, the
  first-run notice before any system change, and removal instructions.

## Applying

SignPath Foundation's conditions are at <https://signpath.org/terms>. They expect:

1. **A published release.** The project must already be released in the form to be signed.
2. **Reputation.** For apps people download and run, they want evidence that it's used and trusted:
   downloads, stars, write-ups or discussions. Apply once there is some.
3. **Two-factor authentication** on GitHub and on SignPath for everyone on the team.
4. **The application form** at <https://signpath.org/apply>. It asks for the maintainer's name and email
   for the SignPath account. These go to SignPath only; don't put them in this repository.

## After acceptance

1. In SignPath, create the project with slug `the-cube` and a release signing policy with slug
   `release-signing` (or change the slugs in `release.yml`). Link it to the predefined *GitHub.com*
   trusted build system, and paste and adjust the artifact configuration.
2. Optionally install the [SignPath GitHub App](https://github.com/apps/signpath) on the repository.
3. In GitHub → Settings → Secrets and variables → Actions:
   - **Secret** `SIGNPATH_API_TOKEN`: an API token of a SignPath user with submitter rights.
   - **Variable** `SIGNPATH_ORGANIZATION_ID`: the SignPath organization ID.
4. Update the README's *Code signing policy* to say releases are signed.
5. Push a tag. Approve the signing request in SignPath, then check the draft release and publish it.
