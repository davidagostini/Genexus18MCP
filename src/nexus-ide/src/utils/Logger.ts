import * as vscode from "vscode";

export type LogLevel = "error" | "warn" | "info" | "debug";

const LEVEL_ORDER: Record<LogLevel, number> = {
  error: 0,
  warn: 1,
  info: 2,
  debug: 3,
};

/** Minimal sink contract so tests can inject a fake instead of a real OutputChannel. */
export interface LogSink {
  appendLine(value: string): void;
}

const CHANNEL_NAME = "GeneXus Nexus IDE";

/**
 * Level-gated logger backed by a single VS Code OutputChannel.
 * Reads its minimum level from the `genexus.logLevel` setting (default "info")
 * and refreshes it on configuration change.
 */
export class Logger {
  private static sink: LogSink | undefined;
  private static level: LogLevel = "info";
  private static configListener: vscode.Disposable | undefined;

  /** Wires the logger to a real OutputChannel and starts watching `genexus.logLevel`. */
  static activate(context: vscode.ExtensionContext): void {
    const channel = vscode.window.createOutputChannel(CHANNEL_NAME);
    context.subscriptions.push(channel);
    Logger.sink = channel;
    Logger.refreshLevel();

    Logger.configListener = vscode.workspace.onDidChangeConfiguration((event) => {
      if (event.affectsConfiguration("genexus.logLevel")) {
        Logger.refreshLevel();
      }
    });
    context.subscriptions.push(Logger.configListener);
  }

  /** Test-only hook: point the logger at a fake sink and/or force a level. */
  static configureForTest(sink: LogSink, level: LogLevel = "info"): void {
    Logger.sink = sink;
    Logger.level = level;
  }

  static show(): void {
    if (Logger.sink && "show" in Logger.sink) {
      (Logger.sink as vscode.OutputChannel).show(true);
    }
  }

  static error(message: string): void {
    Logger.write("error", message);
  }

  static warn(message: string): void {
    Logger.write("warn", message);
  }

  static info(message: string): void {
    Logger.write("info", message);
  }

  static debug(message: string): void {
    Logger.write("debug", message);
  }

  private static refreshLevel(): void {
    const configured = vscode.workspace
      .getConfiguration("genexus")
      .get<LogLevel>("logLevel", "info");
    Logger.level = LEVEL_ORDER[configured] !== undefined ? configured : "info";
  }

  private static write(level: LogLevel, message: string): void {
    if (LEVEL_ORDER[level] > LEVEL_ORDER[Logger.level]) {
      return;
    }
    if (!Logger.sink) {
      return;
    }
    const timestamp = new Date().toISOString();
    Logger.sink.appendLine(`[${timestamp}] [${level.toUpperCase()}] ${message}`);
  }
}
