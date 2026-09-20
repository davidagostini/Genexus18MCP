import * as path from 'path';
import { downloadAndUnzipVSCode, runTests } from '@vscode/test-electron';

async function main() {
	try {
		process.env.NEXUS_IDE_TEST_MODE = '1';

		// The folder containing the Extension Manifest package.json
		// Passed to `--extensionDevelopmentPath`
		const extensionDevelopmentPath = path.resolve(__dirname, '../../');

		// The path to test runner
		// Passed to --extensionTestsPath
		const extensionTestsPath = path.resolve(__dirname, './suite/index');

		const version = process.env.NEXUS_IDE_VSCODE_VERSION || '1.111.0';
		const cachePath = path.resolve(__dirname, '../../.vscode-test');
		const maxAttempts = 4;
		let vscodeExecutablePath: string | undefined;
		let lastDownloadError: unknown;

		for (let attempt = 1; attempt <= maxAttempts; attempt++) {
			try {
				// Download VS Code into the workflow-cached directory. The retry is bounded
				// and only covers transient CDN/network failures.
				vscodeExecutablePath = await downloadAndUnzipVSCode({
					version,
					cachePath,
					timeout: 120000,
				});
				break;
			} catch (err) {
				lastDownloadError = err;
				if (attempt === maxAttempts) throw err;
				const delayMs = Math.min(30000, 2000 * 2 ** (attempt - 1));
				console.warn(`VS Code integration setup failed (attempt ${attempt}/${maxAttempts}); retrying in ${delayMs}ms`, err);
				await new Promise((resolve) => setTimeout(resolve, delayMs));
			}
		}

		if (!vscodeExecutablePath) throw lastDownloadError;

		// Keep functional test failures outside the infrastructure retry loop.
		await runTests({
			extensionDevelopmentPath,
			extensionTestsPath,
			version,
			vscodeExecutablePath,
			timeout: 120000,
			launchArgs: [
				'--disable-updates',
				'--skip-welcome',
				'--skip-release-notes',
				'--enable-proposed-api',
				'lennix1337.nexus-ide',
			],
		});
	} catch (err) {
		console.error('Failed to run tests', err);
		process.exit(1);
	}
}

main();
