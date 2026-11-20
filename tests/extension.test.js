'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');
const root = path.dirname(__dirname);
const manifest = require('../package.json');

function descriptor(configuration, {platform = 'win32', remoteName, exists = true} = {}) {
    let factory;
    const vscode = {
        env: {remoteName},
        debug: {registerDebugAdapterDescriptorFactory(type, value) {
            assert.equal(type, 'desktopclr');
            factory = value;
            return {dispose() {}};
        }},
        DebugAdapterExecutable: class { constructor(command) { this.command = command; } }
    };
    const context = {
        module: {exports: {}}, process: {platform},
        require(name) {
            if (name === 'vscode') return vscode;
            if (name === 'node:fs') return {existsSync: () => exists};
            return require(name);
        }
    };
    vm.runInNewContext(fs.readFileSync(path.join(root, 'extension.js'), 'utf8'), context);
    context.module.exports.activate({subscriptions: [], asAbsolutePath: value => path.join(root, value)});
    return factory.createDebugAdapterDescriptor({configuration});
}

test('launch and attach select the requested desktop worker', () => {
    for (const request of ['launch', 'attach']) {
        for (const architecture of ['x64', 'x86']) {
            assert.equal(descriptor({request, architecture}).command,
                path.join(root, 'bin', architecture, 'DesktopClr.Adapter.exe'));
        }
    }
});

test('unsupported platforms, architecture, and missing installation are explicit errors', () => {
    assert.throws(() => descriptor({}, {platform: 'linux'}), /local Windows/);
    assert.throws(() => descriptor({}, {remoteName: 'ssh-remote'}), /local Windows/);
    assert.throws(() => descriptor({architecture: 'arm64'}), /architecture/);
    assert.throws(() => descriptor({}, {exists: false}), /missing/);
});

test('both configuration surfaces expose the implemented options', () => {
    const configs = manifest.contributes.debuggers[0].configurationAttributes;
    assert.deepEqual(configs.attach.required, ['processId']);
    assert.equal(configs.attach.properties.processId.minimum, 1);
    for (const request of ['launch', 'attach']) {
        const properties = configs[request].properties;
        assert.deepEqual(properties.architecture.enum, ['x64', 'x86']);
        assert.deepEqual(properties.expressionStringComparison.enum, ['binary']);
        assert.equal(properties.sourceFileMap.type, 'object');
        assert.equal(properties.diagnostics.default, false);
    }
});
