#!/usr/bin/env node
"use strict";

const fs = require("node:fs");
const path = require("node:path");
const os = require("node:os");
const cp = require("node:child_process");

function printUsage() {
    console.log(`Usage:
  unifocl-codex-plugin install [--server-name <name>] [--config-root <path>] [--skills-dir <path>]
  unifocl-codex-plugin doctor
  unifocl-codex-plugin help
`);
}

function fail(message) {
    console.error(`error: ${message}`);
    process.exit(1);
}

function run(command, args, options = {}) {
    const result = cp.spawnSync(command, args, {
        stdio: "pipe",
        encoding: "utf8",
        ...options
    });
    return result;
}

function ensureCommandExists(command) {
    const checker = process.platform === "win32" ? "where" : "which";
    const result = run(checker, [command]);
    if (result.status !== 0) {
        fail(`required command not found on PATH: ${command}`);
    }
}

function parseArgs(argv) {
    const positional = [];
    const options = {
        serverName: "unifocl",
        configRoot: "",
        skillsDir: ""
    };

    for (let i = 0; i < argv.length; i += 1) {
        const token = argv[i];
        if (token === "--server-name") {
            options.serverName = argv[++i] || "";
            continue;
        }
        if (token === "--config-root") {
            options.configRoot = argv[++i] || "";
            continue;
        }
        if (token === "--skills-dir") {
            options.skillsDir = argv[++i] || "";
            continue;
        }
        positional.push(token);
    }

    return { positional, options };
}

function resolveCodexHome() {
    if (process.env.CODEX_HOME && process.env.CODEX_HOME.trim() !== "") {
        return path.resolve(process.env.CODEX_HOME);
    }
    return path.join(os.homedir(), ".codex");
}

function copyDirRecursive(srcDir, dstDir) {
    fs.mkdirSync(dstDir, { recursive: true });
    const entries = fs.readdirSync(srcDir, { withFileTypes: true });
    for (const entry of entries) {
        const srcPath = path.join(srcDir, entry.name);
        const dstPath = path.join(dstDir, entry.name);
        if (entry.isDirectory()) {
            copyDirRecursive(srcPath, dstPath);
        } else {
            fs.copyFileSync(srcPath, dstPath);
        }
    }
}

function install(options) {
    ensureCommandExists("codex");
    ensureCommandExists("unifocl");

    const serverName = options.serverName || "unifocl";
    const codexHome = resolveCodexHome();
    const configRoot = options.configRoot && options.configRoot.trim() !== ""
        ? path.resolve(options.configRoot)
        : path.join(codexHome, "unifocl-config");
    const skillsTarget = options.skillsDir && options.skillsDir.trim() !== ""
        ? path.resolve(options.skillsDir)
        : path.join(codexHome, "skills", "unifocl");

    fs.mkdirSync(configRoot, { recursive: true });
    fs.mkdirSync(path.dirname(skillsTarget), { recursive: true });

    const packageRoot = path.resolve(__dirname, "..");
    const bundledSkills = path.join(packageRoot, "skills", "unifocl");
    if (!fs.existsSync(bundledSkills)) {
        fail(`bundled skills not found: ${bundledSkills}`);
    }

    run("codex", ["mcp", "remove", serverName], { stdio: "ignore" });
    const addResult = run("codex", [
        "mcp",
        "add",
        serverName,
        "--env",
        `UNIFOCL_CONFIG_ROOT=${configRoot}`,
        "--",
        "unifocl",
        "--mcp-server"
    ]);

    if (addResult.status !== 0) {
        const stderr = (addResult.stderr || "").trim();
        const stdout = (addResult.stdout || "").trim();
        fail(`failed to register MCP server in Codex.\n${stderr || stdout}`);
    }

    copyDirRecursive(bundledSkills, skillsTarget);

    console.log("installed: unifocl codex plugin");
    console.log(`- mcp server: ${serverName} -> unifocl --mcp-server`);
    console.log(`- config root: ${configRoot}`);
    console.log(`- skills path: ${skillsTarget}`);
    console.log("next: restart Codex session and verify MCP tools are available.");
}

function doctor() {
    const codexHome = resolveCodexHome();
    const skillsPath = path.join(codexHome, "skills", "unifocl");
    console.log(`codex home: ${codexHome}`);
    console.log(`skills path: ${skillsPath}`);
    console.log(`skills installed: ${fs.existsSync(skillsPath) ? "yes" : "no"}`);
}

function main() {
    const { positional, options } = parseArgs(process.argv.slice(2));
    const command = positional[0] || "help";

    if (command === "help" || command === "--help" || command === "-h") {
        printUsage();
        return;
    }
    if (command === "install") {
        install(options);
        return;
    }
    if (command === "doctor") {
        doctor();
        return;
    }

    fail(`unknown command: ${command}`);
}

main();
