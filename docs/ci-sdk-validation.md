# GeneXus SDK CI validation lane

Hosted CI remains the fast, credential-free contract lane. It builds and tests the
Gateway and records the Worker coverage skip when GeneXus is not installed. The
Worker must not be treated as passed on a hosted runner.

The executable SDK lane is a protected, opt-in GitHub Actions job on a Windows
self-hosted runner labelled `gx-sdk-18`. Enable it only with the repository
variable `GXMCP_SDK_CI_ENABLED=true`. The machine must already have a licensed
GeneXus 18 installation and a verified disposable synthetic KB; neither the SDK,
credentials, database dumps, nor licensed artifacts belong in this repository.

Required runner environment:

- `GXMCP_SDK_CI_LICENSE_ACK=1` — administrator acknowledgement that the runner's
  GeneXus installation is licensed for this validation use.
- `GXMCP_SDK_PATH` — absolute SDK installation path.
- `GXMCP_TEST_KB` — absolute path to the disposable KB.
- `GXMCP_TEST_FIXTURE` — attestation manifest matching that KB, as required by
  [`live-kb-test-harness.md`](live-kb-test-harness.md).

The job runs `scripts/ci/sdk-validation.ps1`. It first runs
`scripts/validate-gx-sdk.ps1` against `config/sdk-compatibility.json`, then builds
and runs the existing live Gateway/Worker validation. The script creates an
ignored run directory and isolated config, restores process environment variables,
and removes the directory in `finally`; it never copies or publishes SDK bytes.

The lane writes `status.json` and `sdk-validation.log` under the runner temp
artifact directory. Status is explicit:

- `pass`: fingerprint and live Worker validation completed.
- `skip`: a required licensed-machine or fixture precondition is unavailable.
- `fail`: a precondition or validation failed; the job fails.

The workflow uploads the status and log with retention, and writes the skip reason
to the Actions job summary. A disabled lane is visible as `skipped` rather than
silently implying Worker coverage.
