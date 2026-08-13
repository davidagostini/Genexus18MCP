import * as path from 'path';
import { runTests } from '@vscode/test-electron';

async function main() {
	try {
		process.env.NEXUS_IDE_TEST_MODE = '1';

		// The folder containing the Extension Manifest package.json
		// Passed to `--extensionDevelopmentPath`
		const extensionDevelopmentPath = path.resolve(__dirname, '../../');

		// The path to test runner
		// Passed to --extensionTestsPath
		const extensionTestsPath = path.resolve(__dirname, './suite/index');

		// Download VS Code, unzip it and run the integration test
		await runTests({
			extensionDevelopmentPath,
			extensionTestsPath,
			version: '1.111.0',
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
