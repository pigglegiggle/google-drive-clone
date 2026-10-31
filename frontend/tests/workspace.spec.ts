import { test, expect } from "@playwright/test";
import { createHash, randomUUID } from "node:crypto";

const email = () => `browser-${randomUUID()}@example.test`;
const password = "Browser-test-password-2026";

test("organize, upload, restore a version, share and revoke through the UI", async ({
  page,
  browser,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (e) => errors.push(e.message));
  await page.goto("/");
  await page
    .getByRole("button", { name: "New here? Create an account" })
    .click();
  await page.getByLabel("Email", { exact: true }).fill(email());
  await page.getByLabel("Password", { exact: true }).fill(password);
  await page
    .getByRole("button", { name: "Create account", exact: true })
    .click();
  await expect(
    page.getByRole("heading", { name: "My files", exact: true }),
  ).toBeVisible();
  await page.getByRole("button", { name: "New folder", exact: true }).click();
  await page.getByRole("dialog").getByLabel("Name").fill("Documents");
  await page.getByRole("button", { name: "Save", exact: true }).click();
  await page.getByRole("button", { name: "Documents", exact: true }).click();
  const first = Buffer.from("Quarterly design notes\nVersion one.\n");
  await page
    .getByLabel("Choose files to upload")
    .setInputFiles({
      name: "design.txt",
      mimeType: "text/plain",
      buffer: first,
    });
  await expect(
    page.getByRole("button", { name: "design.txt", exact: true }),
  ).toBeVisible();
  await page.getByRole("button", { name: "design.txt", exact: true }).click();
  const details = page.getByRole("complementary", { name: "File details" });
  await expect(
    details.getByText(createHash("sha256").update(first).digest("hex")),
  ).toBeAttached();
  await details.getByRole("button", { name: "Upload new version" }).click();
  await page
    .getByLabel("Choose files to upload")
    .setInputFiles({
      name: "design.txt",
      mimeType: "text/plain",
      buffer: Buffer.from("Second version."),
    });
  await expect(
    details.getByText("Previous version", { exact: true }),
  ).toBeVisible();
  await details
    .getByRole("button", { name: "Restore version", exact: true })
    .click();
  await expect(
    details.getByText("Version restored", { exact: true }),
  ).toHaveCount(0);
  await expect(page.getByRole("status")).toContainText("Version restored");
  await details.getByRole("button", { name: "Share", exact: true }).click();
  await page.getByRole("button", { name: "Create link", exact: true }).click();
  const link = page.getByLabel("Share this link");
  await expect(link).toHaveValue(/\/share\/[a-f0-9]{64}$/);
  const publicContext = await browser.newContext();
  const publicPage = await publicContext.newPage();
  await publicPage.goto(await link.inputValue());
  await expect(
    publicPage.getByRole("heading", { name: "design.txt" }),
  ).toBeVisible();
  const downloadPromise = publicPage.waitForEvent("download");
  await publicPage.getByRole("link", { name: "Download file" }).click();
  expect((await downloadPromise).suggestedFilename()).toBe("design.txt");
  await page.getByRole("button", { name: "Revoke", exact: true }).click();
  await publicPage.reload();
  await expect(publicPage.getByRole("alert")).toContainText("unavailable");
  await publicContext.close();
  await page.getByRole("button", { name: "Close dialog" }).click();
  await details.getByRole("button", { name: "Rename", exact: true }).click();
  await page.getByRole("dialog").getByLabel("Name").fill("renamed.txt");
  await page.getByRole("button", { name: "Save", exact: true }).click();
  await expect(
    page.getByRole("button", { name: "renamed.txt", exact: true }),
  ).toBeVisible();
  await page.getByRole("button", { name: "renamed.txt", exact: true }).click();
  await details.getByRole("button", { name: "Move to Trash" }).click();
  await page.getByRole("button", { name: "Trash", exact: true }).click();
  await page.getByRole("button", { name: "renamed.txt", exact: true }).click();
  await details.getByRole("button", { name: "Restore", exact: true }).click();
  await expect(page.getByRole("status")).toContainText("Item restored");
  expect(errors).toEqual([]);
});

test("resume confirmed parts after closing the page and reject a different file", async ({
  page,
}) => {
  const account = email();
  await page.request.post("/api/auth/register", {
    data: { email: account, password },
    headers: { "X-Drive-Request": "1" },
  });
  const size = 8 * 1024 * 1024;
  const original = Buffer.concat([Buffer.alloc(size, 65), Buffer.from("tail")]);
  const started = await page.request.post("/api/uploads", {
    data: {
      name: "resume.bin",
      size: original.length,
      sha256: createHash("sha256").update(original).digest("hex"),
    },
    headers: { "X-Drive-Request": "1" },
  });
  expect(started.ok()).toBeTruthy();
  const id = (await started.json()).id;
  const uploaded = await page.request.put(`/api/uploads/${id}/parts/1`, {
    data: original.subarray(0, size),
    headers: {
      "X-Drive-Request": "1",
      "X-Part-SHA256": createHash("sha256")
        .update(original.subarray(0, size))
        .digest("hex"),
      "Content-Type": "application/octet-stream",
    },
  });
  expect(uploaded.ok()).toBeTruthy();
  await page.goto("/");
  await page.getByRole("button", { name: "Resume upload" }).click();
  await page
    .getByLabel("Choose files to upload")
    .setInputFiles({
      name: "wrong.bin",
      mimeType: "application/octet-stream",
      buffer: Buffer.from("wrong"),
    });
  await expect(
    page.getByText(
      "Select the original file: its size and SHA-256 must match the paused upload.",
    ),
  ).toBeVisible();
  const partRequests: string[] = [];
  page.on("request", (request) => {
    if (request.method() === "PUT" && request.url().includes("/parts/"))
      partRequests.push(request.url());
  });
  await page.getByRole("button", { name: "Resume upload" }).click();
  await page
    .getByLabel("Choose files to upload")
    .setInputFiles({
      name: "resume.bin",
      mimeType: "application/octet-stream",
      buffer: original,
    });
  await expect(
    page.getByRole("button", { name: "resume.bin", exact: true }),
  ).toBeVisible();
  expect(partRequests).toHaveLength(1);
  expect(partRequests[0]).toMatch(/\/parts\/2$/);
});

test("mobile layout keeps navigation usable without page overflow", async ({
  page,
}) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.request.post("/api/auth/register", {
    data: { email: email(), password },
    headers: { "X-Drive-Request": "1" },
  });
  await page.goto("/");
  await expect(
    page.getByRole("button", { name: "Upload files", exact: true }),
  ).toBeVisible();
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBeTruthy();
  await page.getByRole("button", { name: "New folder", exact: true }).click();
  await expect(page.getByRole("dialog")).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(page.getByRole("dialog")).toHaveCount(0);
});

test("pause an in-flight transfer, resume it, and move the completed file", async ({
  page,
}) => {
  await page.request.post("/api/auth/register", {
    data: { email: email(), password },
    headers: { "X-Drive-Request": "1" },
  });
  await page.goto("/");
  let sawPart = false;
  await page.route("**/api/uploads/*/parts/*", async (route) => {
    sawPart = true;
    await new Promise((resolve) => setTimeout(resolve, 1200));
    await route.continue().catch(() => {});
  });
  const buffer = Buffer.from("A file that can be paused before confirmation.");
  await page
    .getByLabel("Choose files to upload")
    .setInputFiles({ name: "pause.txt", mimeType: "text/plain", buffer });
  await expect.poll(() => sawPart).toBeTruthy();
  await page.getByRole("button", { name: "Pause", exact: true }).click();
  await expect(
    page.getByRole("button", { name: "Resume upload", exact: true }),
  ).toBeVisible();
  await page.unroute("**/api/uploads/*/parts/*");
  await page
    .getByRole("button", { name: "Resume upload", exact: true })
    .click();
  await page
    .getByLabel("Choose files to upload")
    .setInputFiles({ name: "pause.txt", mimeType: "text/plain", buffer });
  await expect(
    page.getByRole("button", { name: "pause.txt", exact: true }),
  ).toBeVisible();
  await page.getByRole("button", { name: "New folder", exact: true }).click();
  await page.getByRole("dialog").getByLabel("Name").fill("Archive");
  await page.getByRole("button", { name: "Save", exact: true }).click();
  await page.getByRole("button", { name: "pause.txt", exact: true }).click();
  await page
    .getByRole("complementary", { name: "File details" })
    .getByRole("button", { name: "Move", exact: true })
    .click();
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "Archive /", exact: true })
    .click();
  await page.getByRole("button", { name: "Move here", exact: true }).click();
  await expect(
    page.getByRole("button", { name: "pause.txt", exact: true }),
  ).toHaveCount(0);
  await page.getByRole("button", { name: "Archive", exact: true }).click();
  await expect(
    page.getByRole("button", { name: "pause.txt", exact: true }),
  ).toBeVisible();
});
