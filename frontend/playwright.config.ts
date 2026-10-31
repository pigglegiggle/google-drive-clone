import { defineConfig } from "@playwright/test";
export default defineConfig({
  testDir: "./tests",
  timeout: 90000,
  workers: 1,
  reporter: "list",
  use: {
    baseURL: process.env.UI_URL || "http://127.0.0.1:5174",
    headless: true,
    viewport: { width: 1440, height: 960 },
    launchOptions: process.env.CHROME_PATH
      ? { executablePath: process.env.CHROME_PATH }
      : {},
    trace: "retain-on-failure",
  },
});
