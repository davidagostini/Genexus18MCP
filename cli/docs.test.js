const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const repoRoot = path.join(__dirname, '..');
const read = (file) => fs.readFileSync(path.join(repoRoot, file), 'utf8');

function requiredNodeMajor() {
    const range = JSON.parse(read('package.json')).engines.node;
    const match = range.match(/>=([0-9]+)/);
    assert.ok(match, `Unable to determine Node.js minimum from engines.node: ${range}`);
    return match[1];
}

test('onboarding docs stay aligned with package prerequisites', () => {
    const nodeMajor = requiredNodeMajor();
    const readme = read('README.md');
    const spanish = read('docs/GETTING_STARTED.es.md');
    const portuguese = read('docs/GETTING_STARTED.pt-br.md');

    assert.match(readme, new RegExp(`Node\\.js ${nodeMajor}\\+`));
    assert.match(spanish, new RegExp(`Node\\.js ${nodeMajor} o superior`));
    assert.match(portuguese, new RegExp(`Node\\.js ${nodeMajor} ou superior`));
    assert.doesNotMatch(readme, /setup\.bat/);
    assert.match(readme, /`\.\\build\.ps1`/);
    assert.match(readme, /\*\*Windows\*\* \(GeneXus is Windows-only\)/);
    assert.match(readme, /GeneXus 18.*installed locally/);
});
