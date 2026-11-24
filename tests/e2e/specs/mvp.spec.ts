import { copyFile, readFile, stat, utimes } from "node:fs/promises";
import { join } from "node:path";
import { crc32, deflateSync } from "node:zlib";
import {
  expect,
  test,
  type BrowserContext,
  type Page,
  type Route,
} from "@playwright/test";
import { expectAccessible } from "./axe";
import { expectRomDownloadProgress, stageRomDownload } from "./rom-progress";

/**
 * Spec §15.1 "E2E": the MVP smoke path against the real image with the seeded `Basic` set (§15.2), in order:
 * login + forced password change → ingest → browse/search/filter → game detail + downloads → global search +
 * ratings → collections → review resolve/delete → images → Admin edit/move/delete → users and logs, with axe on every page and
 * modal visited. The seeded admin is an Admin; Site users are created by the tests that need them.
 *
 * scripts/test-e2e.ps1 runs it on two stacks at once (playwright.config.ts): `@both` tests sign in and ingest on each,
 * `@shard-b` tests run only on the second, the rest only on the first. A `@shard-b` test may only rely on `@both`
 * state, and must leave nothing a later untagged test sees.
 */

const adminPassword = process.env.E2E_ADMIN_PASSWORD;
if (!adminPassword)
  throw new Error("E2E_ADMIN_PASSWORD is not set (run scripts/test-e2e.ps1).");
// The forced change needs a different password; derived so no credential sits in the repo.
const newPassword = `${adminPassword}-changed`;

test.describe.configure({ mode: "serial" });

let context: BrowserContext;
let page: Page;

// One page for the whole walk (the session cookie carries over); axe needs an explicit context.
test.beforeAll(async ({ browser }) => {
  context = await browser.newContext();
  page = await context.newPage();
});

test.afterAll(async () => {
  await context.close();
});

const dialog = () => page.getByRole("dialog");

/** The seeded Switch base game's bytes, read before ingestion moves the file next to its ZIP (step 28a). */
let switchBase: Buffer;
/** The seeded ticketless `[UPD]` update (game updates B7), read before ingestion. */
let switchUpdate: Buffer;
/** The seeded PlayStation set's audio track, read before ingestion moves it (disc systems B20). */
let discTrack2: Buffer;

/** A real square RGB PNG: the API checks magic bytes and dimensions, and the browser must render it. */
function png(size: number, rgb: [number, number, number]): Buffer {
  const chunk = (type: string, data: Buffer) => {
    const body = Buffer.concat([Buffer.from(type, "ascii"), data]);
    const out = Buffer.alloc(data.length + 12);
    out.writeUInt32BE(data.length, 0);
    body.copy(out, 4);
    out.writeUInt32BE(crc32(body), data.length + 8);
    return out;
  };
  const header = Buffer.alloc(13);
  header.writeUInt32BE(size, 0);
  header.writeUInt32BE(size, 4);
  header[8] = 8; // bit depth
  header[9] = 2; // RGB
  const row = Buffer.from([
    0,
    ...Array.from({ length: size }, () => rgb).flat(),
  ]);
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk("IHDR", header),
    chunk(
      "IDAT",
      deflateSync(Buffer.concat(Array.from({ length: size }, () => row))),
    ),
    chunk("IEND", Buffer.alloc(0)),
  ]);
}

/**
 * L7: a bare `/` restores the last Library filter. A Library URL with a filter parameter wins and is saved, and
 * `page=1` is the default, so this opens the unfiltered Library and forgets the saved filter.
 */
async function openLibrary(): Promise<void> {
  await page.goto("/?page=1");
  await expect(page.getByRole("button", { name: "System: All" })).toBeVisible();
}

/** L1: picks `value` (an option's id) from a Library filter menu. */
async function chooseFilter(
  field: "System" | "Region" | "Letter" | "Sort",
  value: string,
): Promise<void> {
  await page.getByRole("button", { name: new RegExp(`^${field}: `) }).click();
  await page.locator(`.dropdown-menu.show [data-value="${value}"]`).click();
}

/** B7: Admin is the first item of the user menu (the toggle reads the username). */
async function openAdmin(p: Page = page): Promise<void> {
  await p.getByRole("button", { name: "admin", exact: true }).click();
  await p.getByRole("link", { name: "Admin", exact: true }).click();
}

async function openGame(title: string): Promise<void> {
  await openLibrary();
  await page
    .getByRole("searchbox", { name: "Search", exact: true })
    .fill(title);
  await expect(page).toHaveURL(/[?&]q=/);
  // The title link is stretched over the card (spec B21).
  await page.getByRole("link", { name: title, exact: true }).click();
  await expect(
    page.getByRole("heading", { level: 1, name: title }),
  ).toBeVisible();
}

/** Creates a Site user (the create form's default type) on the Admin's users page. */
async function createSiteUser(username: string, initialPassword: string): Promise<void> {
  await page.goto("/admin/users");
  await page.getByRole("button", { name: "Create user" }).click();
  await expect(dialog().getByLabel("Account type")).toHaveValue("site-user");
  await dialog().getByLabel("Username").fill(username);
  await dialog().getByLabel("Initial password").fill(initialPassword);
  await dialog().getByRole("button", { name: "Create" }).click();
  await expect(dialog()).toBeHidden();
  await expect(page.getByLabel(`Account type of ${username}`)).toHaveValue(
    "site-user",
  );
}

/** Signs `username` in on a page of its own browser context and makes the forced password change. */
async function signInNewUser(
  username: string,
  initialPassword: string,
  password: string,
): Promise<{ userContext: BrowserContext; user: Page }> {
  const userContext = await context.browser()!.newContext();
  const user = await userContext.newPage();
  await user.goto("/login");
  await user.getByLabel("Username").fill(username);
  await user.getByLabel("Password").fill(initialPassword);
  await user.getByRole("button", { name: "Sign in" }).click();
  await expect(user).toHaveURL(/\/change-password$/);
  await user.getByLabel("Current password").fill(initialPassword);
  await user.getByLabel("New password", { exact: true }).fill(password);
  await user.getByLabel("Confirm new password").fill(password);
  await user.getByRole("button", { name: "Change password" }).click();
  await expect(user).not.toHaveURL(/\/change-password$/);
  return { userContext, user };
}

test("login + forced password change", { tag: "@both" }, async () => {
  await page.goto("/");
  await expect(page).toHaveURL(/\/login\?returnUrl=/);
  await expectAccessible(page, "login");

  await page.getByLabel("Username").fill("admin");
  await page.getByLabel("Password").fill("wrong-password-for-e2e");
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByRole("alert")).toHaveText(
    "Invalid username or password.",
  );

  await page.getByLabel("Password").fill(adminPassword);
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page).toHaveURL(/\/change-password$/);
  await expectAccessible(page, "change password");

  await page.getByLabel("Current password").fill(adminPassword);
  await page.getByLabel("New password", { exact: true }).fill(newPassword);
  await page.getByLabel("Confirm new password").fill(newPassword);
  await page.getByRole("button", { name: "Change password" }).click();

  await expect(page.getByText("The library is empty.")).toBeVisible();
  await expectAccessible(page, "empty library");

  // B28/B29: no scheduled DAT refresh (it would need GitHub) if a run crosses 04:00.
  const xsrf =
    (await context.cookies()).find((c) => c.name === "XSRF-TOKEN")?.value ?? "";
  const schedule = await page.request.put("/api/admin/dats/settings", {
    data: { enabled: false, time: "04:00" },
    headers: { "X-XSRF-TOKEN": decodeURIComponent(xsrf) },
  });
  expect(schedule.status()).toBe(200);
  expect((await schedule.json()).nextRunAt).toBeNull();
});

test("ingestion of the seeded Basic set", { tag: "@both" }, async () => {
  await page.getByRole("link", { name: "Go to Ingestion" }).click();
  await expect(
    page.getByRole("cell", { name: "91-unmatched.gb", exact: true }),
  ).toBeVisible();
  await expectAccessible(page, "ingestion");

  // B23: Pending / Failed / Last runs are accordion panels, Pending open.
  await expect(
    page.getByRole("button", { name: /^Pending files \(\d+\)$/ }),
  ).toHaveAttribute("aria-expanded", "true");
  // Each stack has its own seeded folder (playwright.config.ts).
  const ingest = test.info().project.metadata.ingest as string | undefined;
  if (!ingest)
    throw new Error("E2E_INGEST is not set (run scripts/test-e2e.ps1).");
  switchBase = await readFile(
    join(ingest, "switch", "01-orbit-expedition.nsp"),
  );
  switchUpdate = await readFile(
    join(
      ingest,
      "switch",
      "04-orbit-expedition [0100000000010000][UPD][1.1.0].nsp",
    ),
  );
  discTrack2 = await readFile(
    join(ingest, "psx", "Disc Runner (USA) (Track 2).bin"),
  );
  await page.getByRole("button", { name: "Import all" }).click();

  await page.getByRole("button", { name: "Last runs" }).click();
  // One panel open at a time: opening Last runs closes Pending.
  await expect(
    page.getByRole("button", { name: /^Pending files \(\d+\)$/ }),
  ).toHaveAttribute("aria-expanded", "false");
  const run = page.getByRole("row").filter({ hasText: "Completed" });
  await expect(run).toContainText(
    "Ingested 38 · Duplicate 1 · Review 3 · Failed 1",
    {
      timeout: 60_000,
    },
  );

  await run.getByRole("link").click();
  await expect(page.getByRole("heading", { name: /Ingest Job/ })).toBeVisible();
  await expect(
    page.getByRole("cell", { name: /92-invalid\.gb/ }),
  ).toBeVisible();
  await expectAccessible(page, "job detail");

  // B23: an old ?tab= link opens its panel, then leaves the URL.
  await page.goto("/ingestion?tab=runs");
  await expect(page.getByRole("button", { name: "Last runs" })).toHaveAttribute(
    "aria-expanded",
    "true",
  );
  await expect(page).toHaveURL(/\/ingestion$/);
  await expect(
    page.getByRole("row").filter({ hasText: "Completed" }),
  ).toBeVisible();
  await expectAccessible(page, "ingestion last runs tab");

  // D4/D5: the invalid file is listed under Failed files with its reason, and deleting it goes through the modal.
  await page.goto("/ingestion");
  await page.getByRole("button", { name: "Failed files (1)" }).click();
  const failed = page.locator("#failed-panel");
  const invalid = failed.getByRole("row").filter({ hasText: "92-invalid.gb" });
  await expect(invalid).toContainText("InvalidHeader");
  await expectAccessible(page, "ingestion with failed files");
  await invalid.getByRole("button", { name: "Delete 92-invalid.gb" }).click();
  await expect(dialog()).toHaveAccessibleName("Delete 92-invalid.gb");
  await expect(dialog()).toContainText("gb/92-invalid.gb");
  await expectAccessible(page, "ingest delete modal");
  await dialog().getByRole("button", { name: "Delete", exact: true }).click();
  await expect(dialog()).toBeHidden();
  await expect(failed.getByText("No failed imports.")).toBeVisible();
});

test("reports and the ingest audit trail", { tag: "@shard-b" }, async () => {
  await page.getByRole("link", { name: "Reports", exact: true }).click();
  // Release editing B15: Reports opens Newly Added.
  await expect(page).toHaveURL(/\/reports\/newly-added$/);
  await page.getByRole("link", { name: "Library Size", exact: true }).click();
  await expect(page).toHaveURL(/\/reports\/library-size$/);
  await expect(page.getByRole("table")).toContainText("Game Boy");
  await expectAccessible(page, "library size report");
  await page
    .getByRole("table")
    .getByRole("link", { name: "Game Boy", exact: true })
    .click();
  await expect(page).toHaveURL(/system=gb/);
  await expect(page.getByRole("table")).toContainText("Orbit Blocks");
  await expectAccessible(page, "library size system detail");
  await page.getByRole("link", { name: "Missing Images", exact: true }).click();
  await expect(page.getByRole("table")).toContainText("Placeholder art");
  await expectAccessible(page, "missing images report");
  await page.getByRole("link", { name: "Duplicates", exact: true }).click();
  await expect(page.getByText("No duplicate games found.")).toBeVisible();
  await expectAccessible(page, "duplicates report");
  // Multiple Releases: Orbit Blocks has three releases; the Switch game's base + DLC is one release (DLC B14).
  await page.getByRole("link", { name: "Multiple Releases", exact: true }).click();
  await expect(page).toHaveURL(/\/reports\/multiple-releases$/);
  await expect(page.getByLabel("Minimum releases")).toBeVisible();
  await expect(
    page.getByRole("table").getByRole("link", { name: "Orbit Blocks", exact: true }),
  ).toBeVisible();
  await expect(
    page.getByRole("table").getByRole("link", { name: "Orbit Expedition", exact: true }),
  ).toHaveCount(0);
  await expectAccessible(page, "multiple releases report");
  // B20: the Admin-only Activity report lists the ingested releases.
  await page.getByRole("link", { name: "Activity", exact: true }).click();
  await expect(
    page
      .locator("#activity-added-panel")
      .getByRole("row")
      .nth(1),
  ).toBeVisible();
  await expectAccessible(page, "activity report");

  await openAdmin();
  await page.getByRole("link", { name: "Auditing", exact: true }).click();
  const ingested = page
    .getByRole("row")
    // B30: the Action cell also holds the system badge, so match its action line.
    .filter({ has: page.getByRole("cell").getByText("Ingested", { exact: true }) });
  // One Ingested event per release: a bin/cue set is one event (B7a).
  await expect(ingested).toHaveCount(35);
  await expect(
    page
      .getByRole("row")
      .filter({
        has: page
          .getByRole("cell")
          .getByText("IngestFileDeleted", { exact: true }),
      }),
  ).toContainText("ingest/gb/_failed/92-invalid.gb");
  await expect(ingested.first()).toContainText("admin");
  await expect(ingested.first()).toContainText("ingest/");
  await expectAccessible(page, "audit trail");

  // B28/B29: the DATs page's two panels (one open at a time), and a DAT row's download.
  await page.getByRole("link", { name: "DATs", exact: true }).click();
  await page.getByRole("button", { name: "DAT files" }).click();
  await expect(
    page.getByRole("button", { name: "DAT files" }),
  ).toHaveAttribute("aria-expanded", "true");
  const datDownload = page.getByRole("link", { name: /^Download / }).first();
  const datFile = await page.request.get(
    (await datDownload.getAttribute("href"))!,
  );
  expect(datFile.status()).toBe(200);
  expect(datFile.headers()["content-disposition"]).toContain("attachment");
  await expectAccessible(page, "admin DATs");
});

test("browse, search and filter the library", async () => {
  await openLibrary();
  await expect(page.getByText("30 games")).toBeVisible();
  await expectAccessible(page, "library grid");
  // S1/L3: systems with ROMs in display-name order after "All systems"; F3/L2: each with its game count, right-aligned.
  await page.getByRole("button", { name: "System: All" }).click();
  const systemMenu = page.locator(".dropdown-menu.show");
  await expect(systemMenu.getByPlaceholder("Filter systems…")).toBeFocused();
  await expect(systemMenu.locator(".dropdown-item").first()).toContainText(
    "All systems",
  );
  await expect(
    systemMenu.locator(".dropdown-item").first().locator(".font-monospace"),
  ).toHaveText("29");
  expect(
    await systemMenu
      .locator("[data-value]")
      .evaluateAll((rows) => rows.map((r) => (r as HTMLElement).dataset.value)),
  ).toEqual([
    "s32x",
    "a2600",
    "a5200",
    "a7800",
    "jaguar",
    "lynx",
    "coleco",
    "c64",
    "gb",
    "gba",
    "gbc",
    "gg",
    "gc",
    "sms",
    "md",
    "n64",
    "nes",
    "switch",
    "psx",
    "psp",
    "saturn",
    "snes",
    "tg16",
    "vb",
    "wii",
    "x360",
  ]);
  await expect(
    systemMenu.locator('[data-value="gb"] .font-monospace'),
  ).toHaveText("3");
  await expect(
    systemMenu.locator('[data-value="gbc"] .font-monospace'),
  ).toHaveText("2");
  await expectAccessible(page, "library with the System menu open");
  await page.keyboard.press("Escape");
  await page.getByRole("button", { name: "Letter: All" }).click();
  await expect(
    page.locator('.dropdown-menu.show [data-value="#"] .font-monospace'),
  ).toHaveText(/^\d+$/);
  await page.keyboard.press("Escape");

  await page
    .getByRole("searchbox", { name: "Search", exact: true })
    .fill("orb");
  await expect(page).toHaveURL(/q=orb/);
  // Orbit Blocks, Orbit Kart 64 and Orbit Expedition (Switch).
  await expect(page.getByText("3 games", { exact: true })).toBeVisible();
  await expect(
    page.getByRole("link", { name: /Orbit Expedition/ }).first(),
  ).toBeVisible();

  await openLibrary();
  await chooseFilter("System", "gbc");
  await expect(page).toHaveURL(/system=gbc/);
  await expect(page.getByText("2 games")).toBeVisible();

  await chooseFilter("System", "gba");
  await expect(page).toHaveURL(/system=gba/);
  await expect(page.getByText("1 game", { exact: true })).toBeVisible();

  // Seeded byte-swapped (.v64): it only shows up if ingestion matched it to the DAT after conversion.
  await chooseFilter("System", "n64");
  await expect(page).toHaveURL(/system=n64/);
  await expect(page.getByText("1 game", { exact: true })).toBeVisible();
  await expect(
    page.getByRole("link", { name: "Orbit Kart 64", exact: true }),
  ).toBeVisible();

  await chooseFilter("System", "nes");
  await expect(page).toHaveURL(/system=nes/);
  await expect(page.getByText("1 game", { exact: true })).toBeVisible();
  await expect(
    page.getByRole("link", { name: "Pixel Pioneer", exact: true }),
  ).toBeVisible();

  // Seeded copier-headered (.smc): it only shows up if ingestion matched it to the DAT after stripping the header.
  await chooseFilter("System", "snes");
  await expect(page).toHaveURL(/system=snes/);
  await expect(page.getByText("1 game", { exact: true })).toBeVisible();
  await expect(
    page.getByRole("link", { name: "Mode Seven Rally", exact: true }),
  ).toBeVisible();

  // Seeded as a loose .bin: it only shows up if ingestion accepted it and matched it to the Atari DAT (A3).
  await chooseFilter("System", "a2600");
  await expect(page).toHaveURL(/system=a2600/);
  await expect(page.getByText("1 game", { exact: true })).toBeVisible();
  await expect(
    page.getByRole("link", { name: "Laser Lanes", exact: true }),
  ).toBeVisible();

  // Seeded as a loose .rom: it only shows up if ingestion accepted it and matched it to the ColecoVision DAT (C4).
  await chooseFilter("System", "coleco");
  await expect(page).toHaveURL(/system=coleco/);
  await expect(page.getByText("1 game", { exact: true })).toBeVisible();
  await expect(
    page.getByRole("link", { name: "Pixel Pursuit", exact: true }),
  ).toBeVisible();

  // G4: the seeded .gg with a TMR SEGA header, matched to the Game Gear DAT.
  await chooseFilter("System", "gg");
  await expect(page).toHaveURL(/system=gg/);
  await expect(page.getByText("1 game", { exact: true })).toBeVisible();
  await expect(
    page.getByRole("link", { name: "Pocket Drift", exact: true }),
  ).toBeVisible();

  // M3: the seeded .sms with a TMR SEGA header, matched to the Master System DAT.
  await chooseFilter("System", "sms");
  await expect(page).toHaveURL(/system=sms/);
  await expect(page.getByText("1 game", { exact: true })).toBeVisible();
  await expect(
    page.getByRole("link", { name: "Harbor Patrol", exact: true }),
  ).toBeVisible();

  // D3: seeded as a loose .gen, matched to the Mega Drive DAT and stored as .md.
  await chooseFilter("System", "md");
  await expect(page).toHaveURL(/system=md/);
  await expect(page.getByText("1 game", { exact: true })).toBeVisible();
  await expect(
    page.getByRole("link", { name: "Turbo Comet", exact: true }),
  ).toBeVisible();

  // X4: the seeded .32x with a Mega Drive-style header, matched to the 32X DAT.
  await chooseFilter("System", "s32x");
  await expect(page).toHaveURL(/system=s32x/);
  await expect(page.getByText("1 game", { exact: true })).toBeVisible();
  await expect(
    page.getByRole("link", { name: "Star Relay", exact: true }),
  ).toBeVisible();

  // T2: seeded as a copier-headered .pce, stripped and matched to the TurboGrafx-16 DAT.
  await chooseFilter("System", "tg16");
  await expect(page).toHaveURL(/system=tg16/);
  await expect(page.getByText("1 game", { exact: true })).toBeVisible();
  await expect(
    page.getByRole("link", { name: "Neon Circuit", exact: true }),
  ).toBeVisible();

  // "Quest, The" is shown as "The Quest" but filed under Q (§5, §6.1).
  await openLibrary();
  await chooseFilter("Letter", "Q");
  await expect(
    page.getByRole("img", { name: "The Quest box art" }),
  ).toBeVisible();

  await page.getByRole("button", { name: "List view" }).click();
  await expect(page.getByRole("table")).toContainText("The Quest");
  await expectAccessible(page, "library list");
  await page.getByRole("button", { name: "Grid view" }).click();

  // L6/L7: a chip per filter; the filter survives a trip to a game and back; Reset clears it.
  await openLibrary();
  await chooseFilter("System", "gb");
  await expect(page).toHaveURL(/system=gb/);
  await expect(page.getByTestId("active-filters")).toContainText(
    "System: Game Boy (3)",
  );
  await expect(
    page.getByRole("button", { name: "System: Game Boy" }),
  ).toBeVisible();
  await page.getByRole("link", { name: "Orbit Blocks", exact: true }).click();
  await expect(
    page.getByRole("heading", { level: 1, name: "Orbit Blocks" }),
  ).toBeVisible();
  await page.getByRole("link", { name: "Back to Library" }).click();
  await expect(page).toHaveURL(/system=gb/);
  await expect(page.getByText("3 games", { exact: true })).toBeVisible();
  // B2: Clear all is in the filter row, usable on a 320px phone and on a desktop.
  const filterRow = page.getByRole("search");
  await expect(
    filterRow.getByRole("button", { name: "Clear all" }),
  ).toBeEnabled();
  const viewport = page.viewportSize()!;
  for (const width of [320, 1280]) {
    await page.setViewportSize({ width, height: 800 });
    for (const name of ["System: Game Boy", "Clear all"]) {
      const control = filterRow.getByRole("button", {
        name,
        exact: name === "Clear all",
      });
      await expect(control).toBeInViewport({ ratio: 1 });
      await control.focus();
      await expect(control).toBeFocused();
    }
    // The filter row itself wraps instead of overflowing (the card grid below is out of scope here).
    expect(
      await filterRow.evaluate(
        (row) =>
          row.scrollWidth <= row.clientWidth &&
          row.getBoundingClientRect().right <= window.innerWidth,
      ),
    ).toBe(true);
  }
  await page.setViewportSize(viewport);
  await page.getByRole("button", { name: "Clear all" }).click();
  await expect(page.getByText("30 games")).toBeVisible();
  await expect(page.getByRole("button", { name: "Clear all" })).toBeDisabled();

  // The card grid fits a 320px phone: no sideways page scroll, and (U6) no card shows its rating row there.
  await expect(page.getByRole("button", { name: "Grid view" })).toHaveAttribute(
    "aria-pressed",
    "true",
  );
  await page.setViewportSize({ width: 320, height: 800 });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
  await expect(
    page.locator('.game-card button[aria-label="Favourite"]').filter({ visible: true }),
  ).toHaveCount(0);
  await page.setViewportSize(viewport);
});

test("game detail + downloads + ROM playback progress", async () => {
  await openGame("Orbit Blocks");
  await expect(page.getByText("Primary", { exact: true })).toBeVisible();
  await expectAccessible(page, "game detail");

  // R1/R2: release cards start collapsed; Header details is in the body.
  const release = page.getByRole("article", {
    name: "Orbit Blocks (World) (Rev 1).gb",
  });
  await expect(
    release.getByRole("button", { name: "Copy CRC32" }),
  ).toBeHidden();
  await release
    .getByRole("button", {
      name: "Show details for Orbit Blocks (World) (Rev 1).gb",
    })
    .click();
  await expect(release.getByText("CRC32")).toBeVisible();
  await expect(
    release.getByRole("button", { name: "Copy CRC32" }),
  ).toBeVisible();
  await expect(release.getByText("Identified by")).toBeVisible();
  await release.getByRole("button", { name: "Header details" }).click();
  await expect(dialog()).toHaveAccessibleName(/^Header — Orbit Blocks .*\.gb$/);
  await expect(dialog().getByText("Logo valid")).toBeVisible();
  await expectAccessible(page, "header modal");
  await page.keyboard.press("Escape");
  await expect(dialog()).toBeHidden();

  const zip = page.waitForEvent("download");
  await page.getByRole("button", { name: "Download ZIP" }).click();
  expect((await zip).suggestedFilename()).toBe("Orbit Blocks.zip");

  // Three releases → a dropdown, the primary (Rev 1) first, each with its size.
  await page.getByRole("button", { name: "Download ROM" }).click();
  const items = page.locator(".dropdown-menu.show .dropdown-item");
  await expect(items).toHaveCount(3);
  await expect(items.first()).toHaveText(
    /^Orbit Blocks \(World\) \(Rev 1\)\.gb — .*KiB$/,
  );
  const rom = page.waitForEvent("download");
  await items.first().click();
  expect((await rom).suggestedFilename()).toBe(
    "Orbit Blocks (World) (Rev 1).gb",
  );

  // E2: the game page's Emulators button opens its system's emulators; focus returns to it on close.
  const emulatorsButton = page.getByRole("button", { name: "Emulators" });
  await emulatorsButton.click();
  await expect(dialog()).toHaveAccessibleName("Game Boy Emulators");
  await dialog().getByRole("button", { name: "Close" }).last().click();
  await expect(dialog()).toBeHidden();
  await expect(emulatorsButton).toBeFocused();

  await expect(page.getByRole("button", { name: "Expand all" })).toBeVisible();
  await page.getByRole("button", { name: "Expand all" }).click();
  await expect(
    page.getByRole("button", { name: "Collapse all" }),
  ).toBeVisible();
  await expectAccessible(page, "game detail with releases expanded");

  // Browser emulators B3–B5, B7, B10: play the primary release in the Gambatte core, as served by the image.
  await stageRomDownload(page, "Orbit Blocks (World) (Rev 1).gb");
  const romDownload = page.waitForResponse((r) =>
    /\/api\/games\/[^/]+\/roms\/\d+\/download$/.test(r.url()),
  );
  await release
    .getByRole("button", { name: "Play Orbit Blocks (World) (Rev 1).gb" })
    .click();
  await expect(dialog()).toHaveAccessibleName(
    "Play Orbit Blocks (World) (Rev 1).gb",
  );
  const emulator = dialog().locator("iframe");
  await expect(emulator).toHaveAttribute(
    "title",
    "Emulator: Orbit Blocks (World) (Rev 1).gb",
  );
  expect((await romDownload).status()).toBe(200);
  const frame = page.frameLocator("iframe");
  await expectRomDownloadProgress(frame);
  await expect(frame.getByText("Start Game")).toBeVisible();
  // EmulatorJS's own UI may not pass axe, so the scan covers the modal chrome only.
  await expectAccessible(page, "emulator modal", "iframe");
  const core = page.waitForResponse((r) =>
    /\/emulator\/vendor\/data\/cores\/gambatte(-legacy)?-wasm\.data$/.test(
      r.url(),
    ),
  );
  await frame.getByText("Start Game").click();
  expect((await core).status()).toBe(200);
  await expect(frame.locator("body")).toHaveAttribute(
    "data-game-started",
    "true",
    { timeout: 30_000 },
  );
  await expect(frame.locator("#game canvas")).toBeVisible();
  await frame.locator("#game").click();
  await page.keyboard.press("Escape");
  await expect(dialog()).toBeVisible();
  // Release editing B23: Close asks the player for the battery save first; this ROM-only game has none.
  const saveWrites: string[] = [];
  const onRequest = (r: { url(): string; method(): string }) => {
    if (/\/api\/games\/[^/]+\/saves/.test(r.url()) && ["POST", "PUT"].includes(r.method()))
      saveWrites.push(`${r.method()} ${r.url()}`);
  };
  page.on("request", onRequest);
  await dialog()
    .locator(".modal-footer")
    .getByRole("button", { name: "Close" })
    .click();
  await expect(dialog()).toBeHidden({ timeout: 15_000 });
  await expect(page.locator("iframe")).toHaveCount(0);
  page.off("request", onRequest);
  expect(saveWrites).toEqual([]);

  // Z1–Z4: the Zip Contents tab lists the ZIP and downloads one entry.
  await page.getByRole("tab", { name: "Zip Contents" }).click();
  await expect(page).toHaveURL(/tab=zip/);
  const folders = page.getByRole("list", { name: "Folders" });
  await expect(
    folders.getByRole("button", { name: "Orbit Blocks.zip", exact: true }),
  ).toHaveAttribute("aria-current", "true");
  await expect(
    page.getByRole("table", { name: "Contents of Orbit Blocks.zip" }),
  ).toContainText("info.json");
  await expectAccessible(page, "zip contents");
  await folders.getByRole("button", { name: "roms", exact: true }).click();
  const contents = page.getByRole("table", { name: "Contents of roms" });
  const romName = "Orbit Blocks (World) (Rev 1).gb";
  await expect(contents).toContainText(romName);
  const gameId = new URL(page.url()).pathname.split("/")[2];
  const listing = await (
    await page.request.get(`/api/games/${gameId}/zip-entries`)
  ).json();
  const entry = listing.entries.find(
    (e: { path: string }) => e.path === `roms/${romName}`,
  );
  const entryDownload = page.waitForEvent("download");
  await contents.getByRole("link", { name: `Download ${romName}` }).click();
  const downloaded = await entryDownload;
  expect(downloaded.suggestedFilename()).toBe(romName);
  const { size } = await stat(await downloaded.path());
  expect(size).toBe(entry.sizeBytes);
  await page.getByRole("tab", { name: /^Releases/ }).click();

  // G2: release cards, no sideways page scroll on a phone (Orbit Blocks has the longest seeded file name).
  await expect(
    page.getByRole("article", { name: "Orbit Blocks (World) (Rev 1).gb" }),
  ).toBeVisible();
  // R1: each release card has a 6px stripe in the system colour.
  await expect(
    page.getByRole("article", { name: "Orbit Blocks (World) (Rev 1).gb" }),
  ).toHaveCSS("border-left-width", "6px");
  const viewport = page.viewportSize()!;
  await page.setViewportSize({ width: 375, height: 812 });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
  await page.setViewportSize(viewport);

  // G1: the information card wraps the longest system name instead of scrolling on a phone.
  await openGame("Mode Seven Rally");
  await page.setViewportSize({ width: 375, height: 812 });
  const info = page.locator(".card", {
    has: page.getByRole("table", { name: "Game information" }),
  });
  await expect(info).toContainText("Super Nintendo Entertainment System");
  expect(
    await info.evaluate((card) => card.scrollWidth <= card.clientWidth),
  ).toBe(true);
  await page.setViewportSize(viewport);
});

test("saves: upload, rename, save state from Play, play it, delete, audit", async () => {
  await openGame("Orbit Blocks");
  const savesTab = () => page.getByRole("tab", { name: "Saves" });
  const rows = () => page.locator("#saves-panel tbody tr");
  await savesTab().click();
  await expect(page).toHaveURL(/[?&]tab=saves/);
  await expect(page.getByText("No saves yet")).toBeVisible();
  await expectAccessible(page, "saves tab");

  // B15: upload a battery save from a file.
  await page.getByRole("button", { name: "Add save" }).click();
  await expect(dialog()).toHaveAccessibleName("Add save");
  await dialog().getByLabel("Battery").check();
  await dialog().getByLabel("Name").fill("Uploaded save");
  await dialog().getByLabel("Description").fill("From the e2e walk");
  await dialog()
    .getByLabel("File")
    .setInputFiles({
      name: "slot.srm",
      mimeType: "application/octet-stream",
      buffer: Buffer.from([1, 2, 3, 4]),
    });
  await expectAccessible(page, "add save modal");
  await dialog()
    .locator(".modal-footer")
    .getByRole("button", { name: "Add save" })
    .click();
  await expect(dialog()).toBeHidden();
  await expect(rows()).toHaveCount(1);
  await expect(rows().first()).toContainText("Uploaded save");
  await expect(rows().first()).toContainText("Battery");

  // B15: rename it.
  await page.getByRole("button", { name: "Edit Uploaded save" }).click();
  await dialog().getByLabel("Name").fill("Renamed save");
  await dialog()
    .locator(".modal-footer")
    .getByRole("button", { name: "Save" })
    .click();
  await expect(dialog()).toBeHidden();
  await expect(rows().first()).toContainText("Renamed save");

  // B18/B19: start the game and copy its state into the library from the Play dialog.
  await page.getByRole("tab", { name: /^Releases/ }).click();
  await page
    .getByRole("button", { name: "Play Orbit Blocks (World) (Rev 1).gb" })
    .click();
  // Release editing B22: the player starts with the user's own uploaded battery save of the release.
  await expect(dialog().locator("iframe")).toHaveAttribute(
    "src",
    /&save=[0-9a-f]{32}&saveKind=battery$/,
  );
  const frame = page.frameLocator("iframe");
  // The ROM download of this release is gated by the staging script installed for the game detail test.
  await expectRomDownloadProgress(frame);
  await expect(frame.getByText("Start Game")).toBeVisible();
  await frame.getByText("Start Game").click();
  await expect(frame.locator("body")).toHaveAttribute(
    "data-game-started",
    "true",
    { timeout: 30_000 },
  );
  const upload = page.waitForResponse(
    (r) =>
      /\/api\/games\/[^/]+\/saves$/.test(r.url()) &&
      r.request().method() === "POST",
  );
  await dialog().getByRole("button", { name: "Save state to library" }).click();
  expect((await upload).status()).toBe(201);
  await expect(page.getByText("Saved to library")).toBeVisible();
  await dialog()
    .locator(".modal-footer")
    .getByRole("button", { name: "Close" })
    .click();
  // B23: the close may store the battery save back over the uploaded one; the rows stay two.
  await expect(page.locator("iframe")).toHaveCount(0, { timeout: 15_000 });

  await savesTab().click();
  await expect(rows()).toHaveCount(2);
  const state = rows().filter({
    hasText: /State \d{4}-\d{2}-\d{2} \d{2}:\d{2}/,
  });
  await expect(state).toHaveCount(1);

  // B16/B17: play that state; the play page loads it at game start.
  const stateName = (await state.locator("td").first().innerText()).trim();
  const saveDownload = page.waitForResponse((r) =>
    /\/api\/games\/[^/]+\/saves\/[0-9a-f]{32}\/download$/.test(r.url()),
  );
  await page.getByRole("button", { name: `Play ${stateName}` }).click();
  await expect(dialog().locator("iframe")).toHaveAttribute(
    "src",
    /&save=[0-9a-f]{32}&saveKind=state$/,
  );
  await expectRomDownloadProgress(frame);
  expect((await saveDownload).status()).toBe(200);
  await expect(frame.getByText("Start Game")).toBeVisible();
  await frame.getByText("Start Game").click();
  await expect(frame.locator("body")).toHaveAttribute(
    "data-game-started",
    "true",
    { timeout: 30_000 },
  );
  await expect(frame.locator("#status")).not.toContainText(
    "Couldn't load the save.",
  );
  await dialog()
    .locator(".modal-footer")
    .getByRole("button", { name: "Close" })
    .click();
  await expect(page.locator("iframe")).toHaveCount(0, { timeout: 15_000 });
  await expect(rows()).toHaveCount(2);

  // B15: delete the renamed save after a confirmation that names it.
  await page.getByRole("button", { name: "Delete Renamed save" }).click();
  await expect(dialog()).toContainText("Renamed save");
  await expectAccessible(page, "delete save modal");
  await dialog()
    .locator(".modal-footer")
    .getByRole("button", { name: "Delete save" })
    .click();
  await expect(rows()).toHaveCount(1);

  // B13: the audit trail.
  await openAdmin();
  await page.getByRole("link", { name: "Auditing", exact: true }).click();
  for (const action of ["SaveAdded", "SaveUpdated", "SaveDeleted"])
    await expect(
      page
        .getByRole("row")
        .filter({ has: page.getByRole("cell").getByText(action, { exact: true }) })
        .first(),
    ).toContainText("Orbit Blocks");
});

test("Switch: external releases and a per-release download", async () => {
  await openLibrary();
  await chooseFilter("System", "switch");
  await expect(page).toHaveURL(/system=switch/);
  await expect(page.getByText("1 game", { exact: true })).toBeVisible();
  await page
    .getByRole("link", { name: "Orbit Expedition", exact: true })
    .click();
  await expect(
    page.getByRole("heading", { level: 1, name: "Orbit Expedition" }),
  ).toBeVisible();

  // G7: base and DLC are each stored next to the ZIP; updates and DLC have their own tabs (game updates B9, DLC B2).
  await expect(page.getByRole("tab", { name: "Releases (1)" })).toHaveAttribute(
    "aria-selected",
    "true",
  );
  await expect(page.getByRole("tab", { name: "DLC (1)" })).toBeVisible();
  await expect(page.getByText("External", { exact: true })).toHaveCount(1);
  await expectAccessible(page, "Switch game detail");

  // G4: the base release; the download is the original file, byte for byte. No updates or DLC (B10, DLC B5).
  const downloadRom = page.getByRole("button", { name: "Download ROM" });
  await expect(downloadRom).not.toHaveClass(/dropdown-toggle/);
  const download = page.waitForEvent("download");
  await downloadRom.click();
  const file = await download;
  expect(file.suggestedFilename()).toBe(
    "Orbit Expedition [0100000000010000][v0].nsp",
  );
  expect((await readFile((await file.path())!)).equals(switchBase)).toBe(true);

  // B9–B11: the Updates tab lists both updates newest first; the [UPD] file is version 1.1.0.
  await page.getByRole("tab", { name: "Updates (2)" }).click();
  await expect(page).toHaveURL(/tab=updates/);
  const updates = page.locator("#releases-panel article");
  await expect(updates).toHaveCount(2);
  await expect(updates.first()).toContainText("Version 1.1.0");
  await expect(updates.first()).toContainText(
    "Orbit Expedition [0100000000010800][v0][1.1.0].nsp",
  );
  await expect(updates.nth(1)).toContainText("[v65536]");
  await expectAccessible(page, "Switch game updates");
  const updateDownload = page.waitForEvent("download");
  await updates
    .first()
    .getByRole("button", {
      name: "Download Orbit Expedition [0100000000010800][v0][1.1.0].nsp",
    })
    .click();
  const updateFile = await updateDownload;
  expect(updateFile.suggestedFilename()).toBe(
    "Orbit Expedition [0100000000010800][v0][1.1.0].nsp",
  );
  expect(
    (await readFile((await updateFile.path())!)).equals(switchUpdate),
  ).toBe(true);

  // DLC B2–B4: the DLC tab lists the one DLC.
  await page.getByRole("tab", { name: "DLC (1)" }).click();
  await expect(page).toHaveURL(/tab=dlc/);
  const dlc = page.locator("#releases-panel article");
  await expect(dlc).toHaveCount(1);
  await expect(dlc.first()).toContainText(
    "Orbit Expedition - Bonus Tracks [0100000000011001][v0].nsp",
  );
  await expectAccessible(page, "Switch game DLC");

  // DLC B10/B13: Global Search finds the game by its DLC name, labelled, and Enter opens the DLC tab.
  await page.keyboard.press("Control+k");
  const search = page.getByRole("dialog");
  await search.getByRole("combobox").fill("Bonus Tracks");
  await expect(search.getByRole("option").first()).toContainText(
    "DLC: Orbit Expedition - Bonus Tracks",
  );
  await page.keyboard.press("Enter");
  await expect(page).toHaveURL(/\/games\/[^/?]+\?tab=dlc$/);
  await expect(page.getByRole("tab", { name: "DLC (1)" })).toHaveAttribute(
    "aria-selected",
    "true",
  );
});

test("PlayStation: a disc set lists its files and downloads a track byte-exact", async () => {
  await openGame("Disc Runner");
  await expect(
    page.getByRole("heading", {
      level: 3,
      name: "Disc Runner (USA) (Track 1).bin",
    }),
  ).toBeVisible();
  await page
    .getByRole("button", {
      name: "Show details for Disc Runner (USA) (Track 1).bin",
    })
    .click();

  // B20: the toggle counts the data track, the cue and Track 2; the list puts the main file first.
  const toggle = page.getByRole("button", { name: "3 files" });
  await expect(toggle).toHaveAttribute("aria-expanded", "false");
  await toggle.click();
  await expect(toggle).toHaveAttribute("aria-expanded", "true");
  // B8: the release body has its own key/value table, so scope to the files list.
  const rows = page.locator(
    '#releases-panel [id^="release-"][id$="-files"] table tbody tr',
  );
  await expect(rows).toHaveCount(3);
  await expect(rows.first()).toContainText("Disc Runner (USA) (Track 1).bin");
  await expectAccessible(page, "disc set file list");

  const download = page.waitForEvent("download");
  await page
    .getByRole("button", { name: "Download Disc Runner (USA) (Track 2).bin" })
    .click();
  const file = await download;
  expect(file.suggestedFilename()).toBe("Disc Runner (USA) (Track 2).bin");
  expect((await readFile((await file.path())!)).equals(discTrack2)).toBe(true);
});

test("Bundle Creator: files from two games fill one working cart", async () => {
  // B1: a cartridge game's main file from its release card.
  await openGame("Pixel Pioneer");
  const addMain = page
    .getByRole("button", { name: /^Add .+ to the bundle$/ })
    .first();
  const mainName = (await addMain.getAttribute("aria-label"))!.replace(
    /^Add (.+) to the bundle$/,
    "$1",
  );
  await addMain.click();
  await expect(
    page.getByRole("button", { name: `${mainName} is in the bundle` }).first(),
  ).toBeVisible();
  await expect(
    page.getByRole("link", { name: "Bundle 1 file in the cart" }),
  ).toBeVisible();

  // B1: a disc set's separately downloadable Track 2 from the release's file list.
  await openGame("Disc Runner");
  await page
    .getByRole("button", {
      name: "Show details for Disc Runner (USA) (Track 1).bin",
    })
    .click();
  await page.getByRole("button", { name: "3 files" }).click();
  await page
    .getByRole("button", {
      name: "Add Disc Runner (USA) (Track 2).bin to the bundle",
    })
    .click();
  await expect(
    page.getByRole("link", { name: "Bundle 2 files in the cart" }),
  ).toBeVisible();

  await page.getByRole("link", { name: "Bundle 2 files in the cart" }).click();
  await expect(
    page.getByRole("heading", { level: 1, name: "Bundle Creator" }),
  ).toBeVisible();
  const rows = page.locator("main table tbody tr");
  await expect(rows).toHaveCount(2);
  await expect(rows.nth(0)).toContainText(mainName);
  await expect(rows.nth(1)).toContainText("Disc Runner (USA) (Track 2).bin");
  await expect(rows.getByText("Available")).toHaveCount(2);
  await expectAccessible(page, "Bundle Creator");

  // B2: the cart is this browser's, per account, and survives a reload.
  await page.reload();
  await expect(rows).toHaveCount(2);
});

test("Bundle Creator: the queue downloads two cross-system files in order, with a manual fallback and retry", async () => {
  // Continues from the cart above: a cartridge main file, then a PlayStation set's Track 2.
  await expect(
    page.getByRole("heading", { level: 1, name: "Bundle Creator" }),
  ).toBeVisible();
  const cartRows = page.locator("main table").first().locator("tbody tr");
  await expect(cartRows.getByText("Available")).toHaveCount(2);
  const firstName = (await cartRows
    .nth(0)
    .locator("td")
    .nth(1)
    .textContent())!.trim();
  const secondName = "Disc Runner (USA) (Track 2).bin";

  // B7: the server is full for the second file, so the run pauses and offers its manual link (B6).
  let issues = 0;
  await page.route("**/api/bundle-downloads", (route) =>
    ++issues === 2
      ? route.fulfill({
          status: 503,
          contentType: "application/problem+json",
          body: JSON.stringify({
            status: 503,
            code: "BundleDownloadsFull",
            title: "Too many downloads",
          }),
        })
      : route.continue(),
  );
  const first = page.waitForEvent("download");
  await page.getByRole("button", { name: "Download queue" }).click();
  expect((await first).suggestedFilename()).toBe(firstName);
  await expect(
    page.getByRole("alert").filter({ hasText: "too many downloads" }),
  ).toBeVisible();
  await expect(page.getByText("1 of 2 sent")).toBeVisible();
  await page.getByText(/Download the remaining files manually/).click();
  await expect(page.getByRole("link", { name: secondName })).toBeVisible();

  // B8: retry sends only the remaining file; the first isn't sent again.
  await page.unroute("**/api/bundle-downloads");
  const second = page.waitForEvent("download");
  await page.getByRole("button", { name: "Retry" }).click();
  expect((await second).suggestedFilename()).toBe(secondName);
  await expect(page.getByText("2 of 2 sent")).toBeVisible();
  const runRows = page
    .getByRole("table", { name: "Download progress" })
    .locator("tbody tr");
  await expect(runRows.getByText("Sent to the browser")).toHaveCount(2);
  expect(issues).toBe(2);
  await expectAccessible(page, "Bundle Creator download");
});

/** Starts the open emulator dialog's game and waits for the core's local .data file and the game to start. */
async function startEmulator(core: string): Promise<void> {
  const frame = page.frameLocator("iframe");
  await expect(frame.getByText("Start Game")).toBeVisible();
  const data = page.waitForResponse((r) =>
    new RegExp(
      `/emulator/vendor/data/cores/${core}(-legacy)?-wasm\\.data$`,
    ).test(r.url()),
  );
  await frame.getByText("Start Game").click();
  expect((await data).status()).toBe(200);
  await expect(frame.locator(".ejs_loading_text")).toHaveCount(0, {
    timeout: 60_000,
  });
  await expect(frame.locator(".ejs_error_text")).toHaveCount(0);
}

async function closeEmulator(): Promise<void> {
  await dialog()
    .locator(".modal-footer")
    .getByRole("button", { name: "Close" })
    .click();
  // Release editing B23: a battery-export core stores the battery save before the dialog goes.
  await expect(dialog()).toBeHidden({ timeout: 15_000 });
  await expect(page.locator("iframe")).toHaveCount(0);
}

test("Play: NES Quick Play from the library uses the local FCEUmm core", { tag: "@shard-b" }, async () => {
  await openLibrary();
  await page
    .getByRole("searchbox", { name: "Search", exact: true })
    .fill("Pixel Pioneer");
  await expect(page).toHaveURL(/[?&]q=/);
  await expect(page.getByText("1 game", { exact: true })).toBeVisible();
  await expect(
    page.getByRole("link", { name: "Pixel Pioneer", exact: true }),
  ).toBeVisible();
  const rom = page.waitForResponse((r) =>
    /\/api\/games\/[^/]+\/roms\/\d+\/download$/.test(r.url()),
  );
  await page.getByRole("button", { name: "Play Pixel Pioneer" }).click();
  await expect(dialog().locator("iframe")).toHaveAttribute(
    "src",
    /core=fceumm&name=Pixel%20Pioneer%20\(USA\)\.nes$/,
  );
  expect((await rom).status()).toBe(200);
  await startEmulator("fceumm");
  await closeEmulator();
});

test("Play: TurboGrafx-16 / PC Engine Quick Play from the library uses the local Beetle PCE core", { tag: "@shard-b" }, async () => {
  await openLibrary();
  await page
    .getByRole("searchbox", { name: "Search", exact: true })
    .fill("Neon Circuit");
  await expect(page).toHaveURL(/[?&]q=/);
  await expect(page.getByText("1 game", { exact: true })).toBeVisible();
  await expect(
    page.getByRole("link", { name: "Neon Circuit", exact: true }),
  ).toBeVisible();
  const bios: string[] = [];
  const track = (r: { url: () => string }) => {
    if (r.url().includes("/api/emulator/bios/")) bios.push(r.url());
  };
  page.on("request", track);
  try {
    const rom = page.waitForResponse((r) =>
      /\/api\/games\/[^/]+\/roms\/\d+\/download$/.test(r.url()),
    );
    await page.getByRole("button", { name: "Play Neon Circuit" }).click();
    await expect(dialog().locator("iframe")).toHaveAttribute(
      "src",
      /core=mednafen_pce&name=Neon%20Circuit%20\(USA\)\.pce$/,
    );
    expect((await rom).status()).toBe(200);
    await startEmulator("mednafen_pce");
  } finally {
    page.off("request", track);
  }
  expect(bios).toEqual([]);
  await closeEmulator();
});

test("Play: a PlayStation set streams its disc ZIP from the detail page and the library", { tag: "@shard-b" }, async () => {
  await openGame("Disc Runner");
  const bios = page.waitForResponse((r) =>
    r.url().endsWith("/api/emulator/bios/scph5501.bin"),
  );
  const disc = page.waitForResponse((r) =>
    /\/api\/games\/[^/]+\/roms\/\d+\/play-disc$/.test(r.url()),
  );
  await page
    .getByRole("button", { name: "Play Disc Runner (USA) (Track 1).bin" })
    .click();
  await expect(dialog().locator("iframe")).toHaveAttribute(
    "src",
    /core=pcsx_rearmed&name=Disc%20Runner%20\(USA\)%20\(Track%201\)\.bin&disc=1$/,
  );
  expect((await bios).status()).toBe(404); // no firmware in the e2e image: the core uses HLE
  const response = await disc;
  expect(response.status()).toBe(200);
  expect(response.headers()["content-type"]).toBe("application/zip");
  expect(response.headers()["cache-control"]).toBe("no-store");
  // Kestrel with synchronous IO off: the stored (uncompressed) ZIP holds the playback cue and both tracks.
  const direct = await page.request.get(response.url());
  expect(direct.status()).toBe(200);
  const zip = await direct.body();
  expect(zip.subarray(0, 4).toString("hex"), `${zip.length} bytes`).toBe(
    "504b0304",
  );
  expect(zip.includes("Disc Runner (USA).cue")).toBe(true);
  expect(
    zip.includes(
      'FILE "track-01.bin" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\nFILE "track-02.bin" BINARY\n  TRACK 02 AUDIO\n',
    ),
  ).toBe(true);
  expect(zip.includes(discTrack2)).toBe(true);
  await expectAccessible(page, "PlayStation emulator modal", "iframe");
  await startEmulator("pcsx_rearmed");
  await closeEmulator();

  await openLibrary();
  await page
    .getByRole("searchbox", { name: "Search", exact: true })
    .fill("Disc Runner");
  await expect(page).toHaveURL(/[?&]q=/);
  await expect(page.getByText("1 game", { exact: true })).toBeVisible();
  await expect(
    page.getByRole("link", { name: "Disc Runner", exact: true }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Play Disc Runner" }).click();
  await expect(dialog().locator("iframe")).toHaveAttribute(
    "src",
    /core=pcsx_rearmed&name=Disc%20Runner%20\(USA\)%20\(Track%201\)\.bin&disc=1$/,
  );
  await closeEmulator();
});

test("Play: a Saturn set asks for saturn_bios.bin, streams its disc and fetches the vendored Yabause core", { tag: "@shard-b" }, async () => {
  // Saturn B6/B7: no firmware in the e2e image (HLE), then play-disc, then the loader. The synthetic disc isn't a real
  // Saturn game, so the case stops once the core payload has arrived instead of waiting for the game to run.
  const order: string[] = [];
  const track = (r: { url: () => string }) => {
    const url = r.url();
    if (url.endsWith("/api/emulator/bios/saturn_bios.bin")) order.push("bios");
    else if (/\/api\/games\/[^/]+\/roms\/\d+\/play-disc$/.test(url))
      order.push("disc");
    else if (url.endsWith("/emulator/vendor/data/loader.js"))
      order.push("loader");
  };
  await openGame("Sky Rally");
  page.on("request", track);
  try {
    const bios = page.waitForResponse((r) =>
      r.url().endsWith("/api/emulator/bios/saturn_bios.bin"),
    );
    const disc = page.waitForResponse((r) =>
      /\/api\/games\/[^/]+\/roms\/\d+\/play-disc$/.test(r.url()),
    );
    await page
      .getByRole("button", { name: "Play Sky Rally (Japan) (Track 1).bin" })
      .click();
    await expect(dialog().locator("iframe")).toHaveAttribute(
      "src",
      /core=yabause&name=Sky%20Rally%20\(Japan\)%20\(Track%201\)\.bin&disc=1$/,
    );
    expect((await bios).status()).toBe(404);
    const response = await disc;
    expect(response.status()).toBe(200);
    expect(response.headers()["content-type"]).toBe("application/zip");
    const frame = page.frameLocator("iframe");
    await expect(frame.getByText("Start Game")).toBeVisible();
    expect(order).toEqual(["bios", "disc", "loader"]);
    const payload = page.waitForResponse((r) =>
      /\/emulator\/vendor\/data\/cores\/yabause(-legacy)?-wasm\.data$/.test(
        r.url(),
      ),
    );
    await frame.getByText("Start Game").click();
    expect((await payload).status()).toBe(200);
  } finally {
    page.off("request", track);
  }
  await closeEmulator();
});

/** PSP B3, MS-DOS P5, 3DS G3: what an isolated core needs, as its tab actually sees it. */
async function isolatedBrowser(psp: Page) {
  return psp.evaluate(() => ({
    secure: isSecureContext,
    isolated: crossOriginIsolated,
    sharedArrayBuffer: typeof SharedArrayBuffer,
    webgl2: document.createElement("canvas").getContext("webgl2") !== null,
  }));
}

test("Play: a PSP UMD image opens the isolated player in a new tab from the detail page and the library", { tag: "@shard-b" }, async () => {
  const tab =
    /\/emulator\/play-psp\.html\?.*core=ppsspp&name=Pocket%20Ninja%20\(USA\)\.iso&disc=1$/;
  await openGame("Pocket Ninja");
  // Observers first: the popup, its document and its authenticated ISO request.
  const popup = context.waitForEvent("page");
  const pageResponse = context.waitForEvent("response", (r) =>
    tab.test(r.url()),
  );
  const iso = context.waitForEvent("response", (r) =>
    /\/api\/games\/[^/]+\/roms\/\d+\/play-disc$/.test(r.url()),
  );
  await page
    .getByRole("link", {
      name: "Play Pocket Ninja (USA).iso (opens in a new tab)",
    })
    .click();
  const psp = await popup;
  await expect(page.locator("iframe")).toHaveCount(0); // no modal: the SPA stays as it was

  const served = await pageResponse;
  expect(served.status()).toBe(200);
  expect(served.headers()["cross-origin-opener-policy"]).toBe("same-origin");
  expect(served.headers()["cross-origin-embedder-policy"]).toBe("require-corp");
  expect(served.headers()["content-security-policy"]).toContain(
    "frame-ancestors 'none'",
  );
  const disc = await iso;
  expect(disc.status()).toBe(200);
  expect(disc.headers()["content-type"]).toBe("application/octet-stream");
  expect(disc.headers()["content-length"]).toBe(String(64 * 1024));
  expect(disc.headers()["accept-ranges"]).toBe("none");
  expect(await isolatedBrowser(psp)).toEqual({
    secure: true,
    isolated: true,
    sharedArrayBuffer: "function",
    webgl2: true,
  });

  // Core startup follows Start Game: the pinned report, threaded payload and asset archive, all local. The synthetic
  // image can't boot, so nothing past the downloads is asserted.
  await expect(psp.getByText("Start Game")).toBeVisible();
  const report = psp.waitForResponse((r) =>
    r.url().endsWith("/emulator/vendor/data/cores/reports/ppsspp.json"),
  );
  const payload = psp.waitForResponse((r) =>
    r.url().endsWith("/emulator/vendor/data/cores/ppsspp-thread-wasm.data"),
  );
  const assets = psp.waitForResponse((r) =>
    r.url().endsWith("/emulator/vendor/data/cores/ppsspp-assets.zip"),
  );
  await psp.getByText("Start Game").click();
  expect((await report).status()).toBe(200);
  expect((await payload).status()).toBe(200);
  expect((await assets).status()).toBe(200);
  await psp.close();

  await openLibrary();
  await page
    .getByRole("searchbox", { name: "Search", exact: true })
    .fill("Pocket Ninja");
  await expect(page).toHaveURL(/[?&]q=/);
  await expect(page.getByText("1 game", { exact: true })).toBeVisible();
  const quick = page.getByRole("link", {
    name: "Play Pocket Ninja (opens in a new tab)",
  });
  await expect(quick).toHaveAttribute("target", "_blank");
  await expect(quick).toHaveAttribute("rel", "noopener");
  const second = context.waitForEvent("page");
  await quick.click();
  const fromLibrary = await second;
  await expect(fromLibrary).toHaveURL(tab);
  await expect(fromLibrary.getByText("Start Game")).toBeVisible();
  expect((await isolatedBrowser(fromLibrary)).isolated).toBe(true);
  await fromLibrary.close();
});

/** Plays a seeded Sega cartridge from its detail page with the bundled SMS Plus GX and waits for a running frame. */
async function playWithSmsPlus(
  title: string,
  fileName: string,
  nameInUrl: RegExp,
): Promise<void> {
  await openGame(title);
  const rom = page.waitForResponse((r) =>
    /\/api\/games\/[^/]+\/roms\/\d+\/download$/.test(r.url()),
  );
  await page.getByRole("button", { name: `Play ${fileName}` }).click();
  await expect(dialog().locator("iframe")).toHaveAttribute("src", nameInUrl);
  expect((await rom).status()).toBe(200);
  await startEmulator("smsplus");
  const frame = page.frameLocator("iframe");
  await expect(frame.locator(".ejs_canvas_parent canvas")).toHaveCount(1);
  await expect(frame.locator(".ejs_error_text")).toHaveCount(0);
  await closeEmulator();
}

test("Play: Genesis explains its missing core; Master System and Game Gear start with SMS Plus GX", { tag: "@shard-b" }, async () => {
  // Genesis B4: the e2e image has no Emulator:GenesisCorePath, so the HEAD check stops before the ROM or loader.
  const genesisRequests: string[] = [];
  const track = (r: { url: () => string }) => {
    if (
      r.url().includes("/api/emulator/cores/") ||
      r.url().includes("/api/emulator/genesis")
    )
      genesisRequests.push(r.url());
  };
  await openGame("Turbo Comet");
  const check = page.waitForResponse(
    (r) =>
      r.url().endsWith("/api/emulator/cores/genesis_plus_gx") &&
      r.request().method() === "HEAD",
  );
  await page
    .getByRole("button", { name: "Play Turbo Comet (USA, Europe).md" })
    .click();
  await expect(dialog().locator("iframe")).toHaveAttribute(
    "src",
    /core=genesis_plus_gx&name=Turbo%20Comet%20\(USA%2C%20Europe\)\.md$/,
  );
  expect((await check).status()).toBe(404);
  await expect(page.frameLocator("iframe").getByRole("status")).toHaveText(
    "Genesis and Sega CD games need the Genesis Plus GX core files: ask an Admin to set them up (Emulator:GenesisCorePath).",
  );
  await expect(page.frameLocator("iframe").getByText("Start Game")).toHaveCount(
    0,
  );
  await closeEmulator();

  // Genesis B6: Master System keeps SMS Plus GX (with the Genesis files absent), and Game Gear its .gg scheme.
  page.on("request", track);
  try {
    await playWithSmsPlus(
      "Harbor Patrol",
      "Harbor Patrol (World).sms",
      /core=smsplus&name=Harbor%20Patrol%20\(World\)\.sms$/,
    );
    await playWithSmsPlus(
      "Pocket Drift",
      "Pocket Drift (World).gg",
      /core=smsplus&name=Pocket%20Drift%20\(World\)\.gg$/,
    );
  } finally {
    page.off("request", track);
  }
  expect(genesisRequests).toEqual([]);
});

test("Play: 32X explains its missing PicoDrive core", { tag: "@shard-b" }, async () => {
  // 32X B3: the e2e image has no Emulator:PicodriveCorePath, so the HEAD check stops before the ROM or loader.
  const later: string[] = [];
  let checked = false;
  const track = (r: { url: () => string }) => {
    if (
      checked &&
      (r.url().includes("/api/emulator/cores/") ||
        // B22: the played record is sent on open and may land after the HEAD; it loads nothing.
        (r.url().includes("/roms/") && !r.url().endsWith("/played")) ||
        r.url().includes("loader.js"))
    )
      later.push(r.url());
  };
  await openGame("Star Relay");
  page.on("request", track);
  try {
    const check = page.waitForResponse(
      (r) =>
        r.url().endsWith("/api/emulator/cores/picodrive") &&
        r.request().method() === "HEAD",
    );
    await page
      .getByRole("button", { name: "Play Star Relay (USA).32x" })
      .click();
    await expect(dialog().locator("iframe")).toHaveAttribute(
      "src",
      /core=picodrive&name=Star%20Relay%20\(USA\)\.32x$/,
    );
    expect((await check).status()).toBe(404);
    checked = true;
    await expect(page.frameLocator("iframe").getByRole("status")).toHaveText(
      "32X games need the PicoDrive core files: ask an Admin to set them up (Emulator:PicodriveCorePath).",
    );
    await expect(
      page.frameLocator("iframe").getByText("Start Game"),
    ).toHaveCount(0);
  } finally {
    page.off("request", track);
  }
  expect(later).toEqual([]);
  await closeEmulator();
});

test("Play: SNES explains its missing Snes9x core from the detail page", { tag: "@shard-b" }, async () => {
  // SNES B3: the e2e image has no Emulator:Snes9xCorePath, so the HEAD check stops before the ROM or loader.
  const later: string[] = [];
  let checked = false;
  const track = (r: { url: () => string }) => {
    if (
      checked &&
      (r.url().includes("/api/emulator/") ||
        // B22: the played record is sent on open and may land after the HEAD; it loads nothing.
        (r.url().includes("/roms/") && !r.url().endsWith("/played")) ||
        r.url().includes("loader.js"))
    )
      later.push(r.url());
  };
  await openGame("Mode Seven Rally");
  page.on("request", track);
  try {
    const check = page.waitForResponse(
      (r) =>
        r.url().endsWith("/api/emulator/cores/snes9x") &&
        r.request().method() === "HEAD",
    );
    await page
      .getByRole("button", { name: "Play Mode Seven Rally (USA).sfc" })
      .click();
    await expect(dialog().locator("iframe")).toHaveAttribute(
      "src",
      /core=snes9x&name=Mode%20Seven%20Rally%20\(USA\)\.sfc$/,
    );
    expect((await check).status()).toBe(404);
    checked = true;
    await expect(page.frameLocator("iframe").getByRole("status")).toHaveText(
      "SNES games need the Snes9x core files: ask an Admin to set them up (Emulator:Snes9xCorePath).",
    );
    await expect(
      page.frameLocator("iframe").getByText("Start Game"),
    ).toHaveCount(0);
  } finally {
    page.off("request", track);
  }
  expect(later).toEqual([]);
  await closeEmulator();
});

test("Play: SNES Quick Play from the library loads the release and the player once the core check passes", { tag: "@shard-b" }, async () => {
  // SNES B1/B3: only the Snes9x HEAD is stubbed; the ROM and the loader are the real ones. Stops before Start Game,
  // since the stock image has no Snes9x payload to serve.
  const coreCheck = "**/api/emulator/cores/snes9x";
  const stub = async (route: Route) =>
    route.request().method() === "HEAD"
      ? route.fulfill({ status: 204 })
      : route.continue();
  const bios: string[] = [];
  const track = (r: { url: () => string }) => {
    if (r.url().includes("/api/emulator/bios/")) bios.push(r.url());
  };
  await page.route(coreCheck, stub);
  page.on("request", track);
  try {
    await openLibrary();
    await page
      .getByRole("searchbox", { name: "Search", exact: true })
      .fill("Mode Seven Rally");
    await expect(page).toHaveURL(/[?&]q=/);
    await expect(page.getByText("1 game", { exact: true })).toBeVisible();
    const check = page.waitForResponse(
      (r) =>
        r.url().endsWith("/api/emulator/cores/snes9x") &&
        r.request().method() === "HEAD",
    );
    const rom = page.waitForResponse((r) =>
      /\/api\/games\/[^/]+\/roms\/\d+\/download$/.test(r.url()),
    );
    const loader = page.waitForResponse((r) =>
      r.url().endsWith("/emulator/vendor/data/loader.js"),
    );
    await page.getByRole("button", { name: "Play Mode Seven Rally" }).click();
    await expect(dialog().locator("iframe")).toHaveAttribute(
      "src",
      /core=snes9x&name=Mode%20Seven%20Rally%20\(USA\)\.sfc$/,
    );
    expect((await check).status()).toBe(204);
    expect((await rom).status()).toBe(200);
    expect((await loader).status()).toBe(200);
    await expect(
      page.frameLocator("iframe").getByText("Start Game"),
    ).toBeVisible();
  } finally {
    page.off("request", track);
    await page.unroute(coreCheck, stub);
  }
  expect(bios).toEqual([]);
  await closeEmulator();
});

const escapeRegExp = (text: string) =>
  text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");

/**
 * B4: plays a seeded Atari cartridge from its detail page with its bundled core and an empty BIOS folder: the real ROM,
 * then after Start Game the same-origin report and a non-threaded payload, with no BIOS request along the way.
 */
async function launchBundled(
  title: string,
  fileName: string,
  core: string,
): Promise<void> {
  const bios: string[] = [];
  const track = (r: { url: () => string }) => {
    if (r.url().includes("/api/emulator/bios/")) bios.push(r.url());
  };
  await openGame(title);
  page.on("request", track);
  try {
    const rom = page.waitForResponse((r) =>
      /\/api\/games\/[^/]+\/roms\/\d+\/download$/.test(r.url()),
    );
    await page.getByRole("button", { name: `Play ${fileName}` }).click();
    await expect(dialog().locator("iframe")).toHaveAttribute(
      "src",
      new RegExp(
        `core=${core}&name=${escapeRegExp(encodeURIComponent(fileName))}$`,
      ),
    );
    expect((await rom).status()).toBe(200);
    const frame = page.frameLocator("iframe");
    await expect(frame.getByText("Start Game")).toBeVisible();
    const origin = new URL(page.url()).origin;
    const report = page.waitForResponse(
      (r) =>
        r.url() === `${origin}/emulator/vendor/data/cores/reports/${core}.json`,
    );
    // Single-threaded only: the modern or the legacy payload, never a "-thread" one.
    const payloads = ["", "-legacy"].map(
      (v) => `${origin}/emulator/vendor/data/cores/${core}${v}-wasm.data`,
    );
    const payload = page.waitForResponse((r) => payloads.includes(r.url()));
    await frame.getByText("Start Game").click();
    expect((await report).status()).toBe(200);
    expect((await payload).status()).toBe(200);
  } finally {
    page.off("request", track);
  }
  expect(bios).toEqual([]);
  await closeEmulator();
}

test("Play: Atari 7800, Jaguar and Lynx launch with their bundled cores", { tag: "@shard-b" }, async () => {
  await launchBundled("Canyon Glide", "Canyon Glide (USA).a78", "prosystem");
  await launchBundled(
    "Cobalt Drift",
    "Cobalt Drift (World).j64",
    "virtualjaguar",
  );
  await launchBundled("Prism Racer", "Prism Racer (USA, Europe).lnx", "handy");
});

test("global search, ratings and favourites", { tag: "@shard-b" }, async () => {
  // Ctrl+K from the library opens the search; part of a title finds it; Enter opens it.
  await openLibrary();
  await expect(page.getByText("30 games")).toBeVisible();
  await page.keyboard.press("Control+k");
  await expect(dialog()).toBeVisible();
  await expect(dialog().getByRole("combobox")).toBeFocused();
  await dialog().getByRole("combobox").fill("orbit");
  await expect(dialog().getByRole("option").first()).toContainText(
    "Orbit Blocks",
  );
  await expectAccessible(page, "global search modal");
  await page.keyboard.press("Enter");
  await expect(
    page.getByRole("heading", { level: 1, name: "Orbit Blocks" }),
  ).toBeVisible();

  // B27: Shift+Enter plays the result: the game page opens with its Play modal, and ?play= leaves the URL.
  await page.keyboard.press("Control+k");
  await dialog().getByRole("combobox").fill("orbit");
  await expect(dialog().getByRole("option").first()).toContainText(
    "Orbit Blocks",
  );
  await page.keyboard.press("Shift+Enter");
  await expect(dialog()).toHaveAccessibleName(/^Play Orbit Blocks/);
  await expect(dialog().locator("iframe")).toHaveCount(1);
  await expect(page).not.toHaveURL(/play=/);
  await expect(page.locator("body")).toHaveClass(/modal-open/);
  await dialog()
    .locator(".modal-footer")
    .getByRole("button", { name: "Close" })
    .click();
  await expect(page.locator("iframe")).toHaveCount(0);

  // S2–S4: reports are found too, first; Enter opens the report.
  await page.keyboard.press("Control+k");
  await dialog().getByRole("combobox").fill("duplic");
  await expect(dialog().getByRole("option").first()).toContainText("Duplicates");
  await page.keyboard.press("Enter");
  await expect(page).toHaveURL(/\/reports\/duplicates$/);

  // Rate and favourite it from its library card; both survive a reload.
  await openLibrary();
  const card = page.getByRole("group", {
    name: "Rating for Orbit Blocks",
    exact: true,
  });
  const star = card.getByRole("button", { name: "Rate 4 of 5" });
  const heart = card.getByRole("button", { name: "Favourite" });
  await star.click();
  await expect(star).toHaveAttribute("aria-pressed", "true");
  await expect(heart).toBeEnabled();
  await heart.click();
  await expect(heart).toHaveAttribute("aria-pressed", "true");
  await expect(heart).toBeEnabled();
  await page.reload();
  await expect(star).toHaveAttribute("aria-pressed", "true");
  await expect(heart).toHaveAttribute("aria-pressed", "true");

  // The Favourites filter shows only it.
  await page.getByRole("button", { name: "Favourites" }).click();
  await expect(page).toHaveURL(/favourite=true/);
  await expect(page.getByText("1 game", { exact: true })).toBeVisible();
  await expect(
    page.getByRole("link", { name: "Orbit Blocks", exact: true }),
  ).toBeVisible();

  // Back to the seed state for the later tests.
  await openLibrary();
  await star.click();
  await expect(star).toHaveAttribute("aria-pressed", "false");
  await expect(heart).toBeEnabled();
  await heart.click();
  await expect(heart).toHaveAttribute("aria-pressed", "false");
  await expect(heart).toBeEnabled();
  await page.reload();
  await expect(star).toHaveAttribute("aria-pressed", "false");
  await expect(heart).toHaveAttribute("aria-pressed", "false");
});

test("collections: create, add from game page and bulk select, search, remove, delete", { tag: "@shard-b" }, async () => {
  await page.goto("/collections");
  await expect(page.getByText("You have no collections yet.")).toBeVisible();
  await expectAccessible(page, "collections page (empty)");

  // B23: create with Markdown notes and an uploaded cover, then land on the new collection.
  await page.getByRole("button", { name: "New collection" }).first().click();
  await expect(dialog()).toHaveAccessibleName("New collection");
  await dialog().getByLabel("Name").fill("Puzzle picks");
  await dialog().getByLabel("Notes (Markdown)").fill("The **best** puzzles");
  await dialog()
    .getByLabel("Cover image file")
    .setInputFiles({
      name: "cover.png",
      mimeType: "image/png",
      buffer: png(32, [40, 120, 200]),
    });
  await expectAccessible(page, "collection modal");
  await dialog().getByRole("button", { name: "Create" }).click();
  await expect(page).toHaveURL(/\/collections\/[0-9a-f-]{36}$/);
  await expect(
    page.getByRole("heading", { level: 1, name: "Puzzle picks" }),
  ).toBeVisible();
  await expect(
    page.getByTestId("collection-notes").locator("strong"),
  ).toHaveText("best");
  await expect(
    page.getByRole("img", { name: "Puzzle picks cover" }),
  ).toBeVisible();

  // B25: add Orbit Blocks from its game page; a badge links back.
  await openGame("Orbit Blocks");
  await page.getByRole("button", { name: "Collections", exact: true }).click();
  await dialog()
    .getByRole("checkbox", { name: /^Puzzle picks/ })
    .check();
  await expect(
    page.getByRole("list", { name: "In collections" }).getByRole("link"),
  ).toHaveText("Puzzle picks");
  await expectAccessible(page, "game collections modal");
  await page.keyboard.press("Escape");
  await expect(dialog()).toBeHidden();

  // B26: add a game from another system with the Library's bulk select.
  await openLibrary();
  await chooseFilter("System", "n64");
  await expect(page).toHaveURL(/system=n64/);
  await page.getByRole("button", { name: "Select", exact: true }).click();
  await page.getByRole("checkbox", { name: "Select Orbit Kart 64" }).check();
  await page.getByRole("button", { name: /Add to collection/ }).click();
  await dialog()
    .getByRole("radio", { name: /^Puzzle picks/ })
    .check();
  await dialog().getByRole("button", { name: "Add", exact: true }).click();
  await expect(page.getByText("Added 1 game to Puzzle picks")).toBeVisible();

  // B27: global search finds the collection; Enter opens it.
  await page.keyboard.press("Control+k");
  await dialog().getByRole("combobox").fill("puzzle");
  await expect(dialog().getByRole("option").first()).toContainText(
    "Puzzle picks",
  );
  await page.keyboard.press("Enter");
  await expect(
    page.getByRole("heading", { level: 1, name: "Puzzle picks" }),
  ).toBeVisible();
  await expect(
    page.getByRole("link", { name: "Orbit Blocks", exact: true }),
  ).toBeVisible();
  await expect(
    page.getByRole("link", { name: "Orbit Kart 64", exact: true }),
  ).toBeVisible();
  await expectAccessible(page, "collection page");

  // P1–P4: Add games leaves out members; results show a cover and a short system name.
  const addGames = page.getByLabel("Add games");
  await addGames.fill("orbit");
  const results = page.locator(".list-group").getByRole("button");
  // Orbit Expedition is suggested; both members stay excluded.
  await expect(results.filter({ hasText: "Orbit Expedition" })).toBeVisible();
  await expect(results.filter({ hasText: "Orbit Blocks" })).toHaveCount(0);
  await expect(results.filter({ hasText: "Orbit Kart 64" })).toHaveCount(0);
  await addGames.fill("zzz no such game");
  await expect(page.getByText("No matching games.")).toBeVisible();
  await addGames.fill("pocket");
  const drift = results.filter({ hasText: "Pocket Drift" });
  await expect(drift).toBeVisible();
  await expect(drift.locator("img")).toHaveCount(1);
  await expect(drift.locator(".badge")).toHaveText("GG");
  await expect(drift.locator(".badge")).toHaveAttribute("title", "Game Gear");
  await addGames.fill("");

  // B24: remove one game.
  await page
    .getByRole("button", { name: "Remove Orbit Blocks from collection" })
    .click();
  await expect(
    page.getByRole("link", { name: "Orbit Blocks", exact: true }),
  ).toBeHidden();
  await expect(
    page.getByRole("link", { name: "Orbit Kart 64", exact: true }),
  ).toBeVisible();

  await page.goto("/collections");
  await expect(page.getByRole("link", { name: "Puzzle picks" })).toBeVisible();
  await expect(page.getByText("1 game", { exact: true })).toBeVisible();
  await expectAccessible(page, "collections page");

  // B5: delete it; the games stay in the Library.
  await page.getByRole("link", { name: "Puzzle picks" }).click();
  await page.getByRole("button", { name: "Delete", exact: true }).click();
  await expect(dialog()).toHaveAccessibleName("Delete Puzzle picks?");
  await dialog().getByRole("button", { name: "Delete", exact: true }).click();
  await expect(page).toHaveURL(/\/collections$/);
  await expect(page.getByText("You have no collections yet.")).toBeVisible();
  await openLibrary();
  await expect(page.getByText("30 games")).toBeVisible();
});

test("collections discovery: browse publishers and genres across systems, share a selection", { tag: "@shard-b" }, async () => {
  // Discovery B1: link tabs; a bare URL is User collections.
  await page.goto("/collections");
  const tabs = page.getByRole("navigation", { name: "Collections views" });
  await expect(
    tabs.getByRole("link", { name: "User collections" }),
  ).toHaveAttribute("aria-current", "page");
  await tabs.getByRole("link", { name: "Publishers" }).click();
  await expect(page).toHaveURL(/\/collections\?tab=publishers$/);
  await expect(tabs.getByRole("link", { name: "Publishers" })).toHaveAttribute(
    "aria-current",
    "page",
  );
  await expect(
    page.getByText("Choose a publisher to see its games across every system."),
  ).toBeVisible();
  await expectAccessible(page, "publishers tab");

  // B2: the seeded publisher groups games from every system.
  const publishers = page.getByRole("list", { name: "Publishers" });
  await publishers.getByRole("link", { name: /Fixture Works/ }).click();
  await expect(page).toHaveURL(/tab=publishers&key=[0-9a-f]{64}$/);
  await expect(
    page.getByRole("heading", { name: /Fixture Works/ }),
  ).toBeVisible();
  await expect(
    page.getByRole("link", { name: "Orbit Kart 64", exact: true }),
  ).toBeVisible();
  await expectAccessible(page, "publisher games");

  // Genres: Racing spans N64, SNES and Game Gear; a game link opens its page.
  await tabs.getByRole("link", { name: "Genres" }).click();
  await expect(page).toHaveURL(/\/collections\?tab=genres$/);
  await page
    .getByRole("list", { name: "Genres" })
    .getByRole("link", { name: /Racing/ })
    .click();
  await expect(page.getByRole("heading", { name: /Racing/ })).toBeVisible();
  for (const title of ["Orbit Kart 64", "Mode Seven Rally", "Pocket Drift"])
    await expect(
      page.getByRole("link", { name: title, exact: true }),
    ).toBeVisible();
  await expect(
    page.getByRole("link", { name: "Orbit Blocks", exact: true }),
  ).toBeHidden();
  const racing = page.url();

  // Back and forward restore the selection.
  await page.goBack();
  await expect(page).toHaveURL(/\/collections\?tab=genres$/);
  await expect(
    page.getByText("Choose a genre to see its games across every system."),
  ).toBeVisible();
  await page.goForward();
  await expect(page.getByRole("heading", { name: /Racing/ })).toBeVisible();

  // A shared link opens the selection directly.
  await page.goto("/collections");
  await page.goto(racing);
  await expect(page.getByRole("link", { name: /Racing/ })).toHaveAttribute(
    "aria-current",
    "true",
  );
  await expect(
    page.getByRole("link", { name: "Pocket Drift", exact: true }),
  ).toBeVisible();
  await page.getByRole("link", { name: "Pocket Drift", exact: true }).click();
  await expect(page).toHaveURL(/\/games\/[0-9a-f-]{36}$/);

  // A selection no game has gets its own empty state.
  await page.goto(`/collections?tab=genres&key=${"0".repeat(64)}`);
  await expect(
    page.getByText("No games have this genre any more."),
  ).toBeVisible();
  await page.getByRole("link", { name: "Back to all genres" }).click();
  await expect(page).toHaveURL(/\/collections\?tab=genres$/);
});

test("collections systems: browse a system's games and its emulator links", { tag: "@shard-b" }, async () => {
  // Systems tab B1/B4: a link tab that asks for a system.
  await page.goto("/collections");
  const tabs = page.getByRole("navigation", { name: "Collections views" });
  await tabs.getByRole("link", { name: "Systems" }).click();
  await expect(page).toHaveURL(/\/collections\?tab=systems$/);
  await expect(tabs.getByRole("link", { name: "Systems" })).toHaveAttribute(
    "aria-current",
    "page",
  );
  await expect(
    page.getByText("Choose a system to see its games and emulators."),
  ).toBeVisible();

  // B3/B5: choosing Game Boy shows its games.
  await page
    .getByRole("list", { name: "Systems" })
    .getByRole("link", { name: /^Game Boy\s+\d+ games?$/ })
    .click();
  await expect(page).toHaveURL(/\/collections\?tab=systems&system=gb$/);
  await expect(
    page.getByRole("link", { name: "Orbit Blocks", exact: true }),
  ).toBeVisible();
  await expectAccessible(page, "system games");

  // B7: the Emulators modal links to project sites in a new tab.
  const emulators = page.getByRole("button", { name: "Emulators" });
  await emulators.click();
  await expect(dialog()).toHaveAccessibleName("Game Boy Emulators");
  const mgba = dialog()
    .getByRole("list", { name: "Windows" })
    .getByRole("link", { name: "mGBA (opens in a new tab)" });
  await expect(mgba).toHaveAttribute("href", /^https:\/\//);
  await expect(mgba).toHaveAttribute("target", "_blank");
  await expectAccessible(page, "emulators modal");
  await dialog().getByRole("button", { name: "Close" }).last().click();
  await expect(dialog()).toBeHidden();
  await expect(emulators).toBeFocused();
});

test("collections discovery: an Admin replaces and removes a publisher logo a Site user only sees", { tag: "@shard-b" }, async () => {
  // A Site user next to the admin, in a browser of their own.
  const curatorPassword = `${newPassword}-curator`;
  await createSiteUser("curator", `${curatorPassword}-initial`);
  const { userContext: curatorContext, user: curator } = await signInNewUser(
    "curator",
    `${curatorPassword}-initial`,
    curatorPassword,
  );
  try {
    const logoOf = (p: Page) =>
      p
        .getByRole("list", { name: "Publishers" })
        .getByRole("link", { name: /Fixture Works/ })
        .locator("img");
    const changeLogo = (p: Page) =>
      p.getByRole("button", { name: "Change logo for Fixture Works" });
    const upload = async (
      size: number,
      rgb: [number, number, number],
      before?: () => Promise<void>,
    ) => {
      await changeLogo(page).click();
      await expect(dialog()).toHaveAccessibleName("Logo for Fixture Works");
      await before?.();
      // B19: the dialog opens on URL; a file upload needs no outbound network.
      await dialog().getByText("File", { exact: true }).click();
      await dialog()
        .getByLabel("Logo image file")
        .setInputFiles({
          name: "logo.png",
          mimeType: "image/png",
          buffer: png(size, rgb),
        });
      await dialog().getByRole("button", { name: "Save logo" }).click();
      await expect(dialog()).toBeHidden();
    };

    // B5: the admin adds the shared logo; Fixture Works has no starter art, so it showed text only.
    await page.goto("/collections?tab=publishers");
    await expect(logoOf(page)).toHaveCount(0);
    await upload(48, [200, 60, 40], async () => {
      await expect(
        dialog().getByText("No logo: the name shows as text."),
      ).toBeVisible();
      await expectAccessible(page, "publisher logo modal");
    });
    await expect(logoOf(page)).toHaveAttribute(
      "src",
      /\/api\/discovery\/publishers\/logo\?key=[0-9a-f]{64}&v=\d+$/,
    );
    const firstVersion = await logoOf(page).getAttribute("src");

    // Shared: the Site user sees it, but has no Change logo (U3: logos are Admin-only).
    await curator.goto("/collections?tab=publishers");
    await expect(logoOf(curator)).toHaveAttribute("src", firstVersion!);
    await expect
      .poll(() =>
        logoOf(curator).evaluate((img: HTMLImageElement) => img.naturalWidth),
      )
      .toBe(48);
    await expect(changeLogo(curator)).toHaveCount(0);

    // The admin replaces it; the Site user gets the new one.
    await upload(64, [40, 160, 90]);
    await expect(logoOf(page)).not.toHaveAttribute("src", firstVersion!);
    const secondVersion = await logoOf(page).getAttribute("src");
    await curator.reload();
    await expect(logoOf(curator)).toHaveAttribute("src", secondVersion!);
    await expect
      .poll(() =>
        logoOf(curator).evaluate((img: HTMLImageElement) => img.naturalWidth),
      )
      .toBe(64);

    // The admin removes it; the text fallback shows again for both.
    await changeLogo(page).click();
    await expect(
      dialog().getByText("Shared logo, set by a user."),
    ).toBeVisible();
    await dialog()
      .getByRole("button", { name: /Remove shared logo/ })
      .click();
    await expect(dialog()).toBeHidden();
    await expect(logoOf(page)).toHaveCount(0);
    await curator.reload();
    await expect(
      curator.getByRole("link", { name: /Fixture Works/ }),
    ).toBeVisible();
    await expect(logoOf(curator)).toHaveCount(0);
  } finally {
    await curatorContext.close();
  }
});

test("user management: a Site user's account, private saves and audit trail", { tag: "@shard-b" }, async () => {
  // Two Site users only this test uses; it relies on @both state only (the ingested library).
  const visitor = "visitor-a";
  const other = "visitor-b";
  const visitorPassword = `${newPassword}-visitor`;
  const otherPassword = `${newPassword}-other`;
  await createSiteUser(visitor, `${visitorPassword}-initial`);
  await expectAccessible(page, "users with a site user");
  await createSiteUser(other, `${otherPassword}-initial`);

  const visitorContext = await context.browser()!.newContext();
  const user = await visitorContext.newPage();
  const otherSession = await signInNewUser(
    other,
    `${otherPassword}-initial`,
    otherPassword,
  );
  try {
    // A wrong password fails (SignInFailed); the right one asks for a new password, which must differ (U7).
    await user.goto("/login");
    await user.getByLabel("Username").fill(visitor);
    await user.getByLabel("Password").fill("not-the-password-for-e2e");
    await user.getByRole("button", { name: "Sign in" }).click();
    await expect(user.getByRole("alert")).toHaveText(
      "Invalid username or password.",
    );
    await user.getByLabel("Password").fill(`${visitorPassword}-initial`);
    await user.getByRole("button", { name: "Sign in" }).click();
    await expect(user).toHaveURL(/\/change-password$/);
    await user.getByLabel("Current password").fill(`${visitorPassword}-initial`);
    await user
      .getByLabel("New password", { exact: true })
      .fill(`${visitorPassword}-initial`);
    await user.getByLabel("Confirm new password").fill(`${visitorPassword}-initial`);
    await user.getByRole("button", { name: "Change password" }).click();
    await expect(
      user.getByText("New password must be different from the current password."),
    ).toBeVisible();
    await expect(user).toHaveURL(/\/change-password$/);
    await user.getByLabel("New password", { exact: true }).fill(visitorPassword);
    await user.getByLabel("Confirm new password").fill(visitorPassword);
    await user.getByRole("button", { name: "Change password" }).click();
    await expect(user).not.toHaveURL(/\/change-password$/);

    // U3/U4: no Admin pages and no library edits.
    await expect(
      user.getByRole("link", { name: "Admin", exact: true }),
    ).toHaveCount(0);
    await user.goto("/?page=1");
    await user
      .getByRole("searchbox", { name: "Search", exact: true })
      .fill("Orbit Blocks");
    // Wait for the debounced search to reach the URL, or its late navigation undoes the click.
    await expect(user).toHaveURL(/[?&]q=/);
    await user.getByRole("link", { name: "Orbit Blocks", exact: true }).click();
    await expect(
      user.getByRole("heading", { level: 1, name: "Orbit Blocks" }),
    ).toBeVisible();
    // B3: a Site user's Manage menu holds only View info.json; no quick edit.
    await user.getByRole("button", { name: "Manage" }).click();
    await expect(user.getByRole("button", { name: "View info.json" })).toBeVisible();
    await expect(user.getByRole("button", { name: "Edit Metadata" })).toHaveCount(0);
    await expect(user.getByRole("button", { name: "Quick edit" })).toHaveCount(0);
    await user.keyboard.press("Escape");
    await expect(user.getByRole("button", { name: /^Add note for / })).toHaveCount(0);
    await expectAccessible(user, "game detail as a site user");

    // S2/S4: the Site user uploads a save; the Admin sees it with its owner, the other Site user doesn't.
    await user.getByRole("tab", { name: "Saves" }).click();
    await user.getByRole("button", { name: "Add save" }).click();
    const userDialog = user.getByRole("dialog");
    await userDialog.getByLabel("Name").fill("Visitor save");
    await userDialog
      .getByLabel("File")
      .setInputFiles({
        name: "slot.state",
        mimeType: "application/octet-stream",
        buffer: Buffer.from([9, 8, 7]),
      });
    await userDialog
      .locator(".modal-footer")
      .getByRole("button", { name: "Add save" })
      .click();
    await expect(userDialog).toBeHidden();
    await expect(user.locator("#saves-panel tbody tr")).toContainText("Visitor save");
    await expect(user.getByRole("columnheader", { name: "Owner" })).toHaveCount(0);

    const otherUser = otherSession.user;
    await otherUser.goto(user.url());
    await expect(
      otherUser.getByRole("heading", { level: 1, name: "Orbit Blocks" }),
    ).toBeVisible();
    await expect(otherUser.getByText("No saves yet")).toBeVisible();

    await page.goto(user.url());
    const ownerRow = page
      .locator("#saves-panel tbody tr")
      .filter({ hasText: "Visitor save" });
    await expect(page.getByRole("columnheader", { name: "Owner" })).toBeVisible();
    await expect(ownerRow).toContainText(visitor);
    await expectAccessible(page, "saves tab with an owner column");

    // U8: disabling the user ends their session on the next request.
    await page.goto("/admin/users");
    await page.getByRole("button", { name: `Disable ${visitor}` }).click();
    await expect(
      page.getByRole("row").filter({ hasText: visitor }).getByText("Disabled"),
    ).toBeVisible();
    await user.goto("/");
    await expect(user).toHaveURL(/\/login/);

    // U9/U10: the audit trail lists the account events (the User filter is the actor; `user=<name>` finds actions on them).
    // B30: the action is the first line of its cell (the system badge sits under it).
    const actions = (p: Page) => p.getByRole("row").getByRole("cell").locator("div");
    await expect(async () => {
      await page.goto(`/admin/audit?user=${visitor}`);
      for (const action of ["SignedIn", "SignInFailed", "PasswordChanged"])
        await expect(
          actions(page).filter({ hasText: new RegExp(`^${action}$`) }).first(),
        ).toBeVisible({ timeout: 1_000 });
    }).toPass();
    await page.goto(`/admin/audit?q=${encodeURIComponent(`user=${visitor}`)}`);
    for (const action of ["UserCreated", "UserDisabled"])
      await expect(
        actions(page).filter({ hasText: new RegExp(`^${action}$`) }).first(),
      ).toBeVisible();
    await expectAccessible(page, "audit trail with account events");
  } finally {
    await visitorContext.close();
    await otherSession.userContext.close();
  }
});

// MS-DOS: untagged and before the review-queue tests, so the DOS item is resolved before "Nothing to review." is
// asserted. It doesn't wait for the emulator: the seeded DEMO.COM only exits.
test("Play: a DOS game ZIP goes through Manual review and opens the isolated DOS player in a new tab", async () => {
  await page.goto("/review");
  await page.getByRole("link", { name: "Dos Demo (1993)(RnRom).zip" }).click();
  // N6/N7: no DAT, so no Suggestions tab; Manual is open with the title from the file name.
  await expect(page.getByRole("tab", { name: "Suggestions" })).toHaveCount(0);
  await expect(page.getByRole("tab", { name: "Manual" })).toHaveAttribute(
    "aria-selected",
    "true",
  );
  await expect(page.getByLabel("Title")).toHaveValue("Dos Demo");
  await page.getByRole("button", { name: "Add manually" }).click();
  await expect(page).toHaveURL(/\/games\//);

  const tab =
    /\/emulator\/play-dos\.html\?.*core=dosbox_pure&name=Dos%20Demo\.dosz$/;
  const popup = context.waitForEvent("page");
  const pageResponse = context.waitForEvent("response", (r) =>
    tab.test(r.url()),
  );
  const download = context.waitForEvent("response", (r) =>
    /\/api\/games\/[^/]+\/roms\/\d+\/download$/.test(r.url()),
  );
  await page
    .getByRole("link", { name: "Play Dos Demo.dosz (opens in a new tab)" })
    .click();
  const dos = await popup;
  await expect(page.locator("iframe")).toHaveCount(0); // no modal: the SPA stays as it was
  expect(dos.url()).toMatch(tab);

  const served = await pageResponse;
  expect(served.status()).toBe(200);
  expect(served.headers()["cross-origin-opener-policy"]).toBe("same-origin");
  expect(served.headers()["cross-origin-embedder-policy"]).toBe("require-corp");
  expect(served.headers()["content-security-policy"]).toContain(
    "frame-ancestors 'none'",
  );
  expect((await download).status()).toBe(200);
  await dos.close();
});

test("Play: a 3DS release opens the isolated 3DS player in a new tab", async () => {
  await openGame("Zephyr Gate");
  const tab =
    /\/emulator\/play-3ds\.html\?.*core=azahar&name=Zephyr%20Gate%20\(Europe\)\.3ds$/;
  const popup = context.waitForEvent("page");
  const pageResponse = context.waitForEvent("response", (r) =>
    tab.test(r.url()),
  );
  const download = context.waitForEvent("response", (r) =>
    /\/api\/games\/[^/]+\/roms\/\d+\/download$/.test(r.url()),
  );
  await page
    .getByRole("link", {
      name: "Play Zephyr Gate (Europe).3ds (opens in a new tab)",
    })
    .click();
  const threeDs = await popup;
  await expect(page.locator("iframe")).toHaveCount(0); // no modal: the SPA stays as it was
  expect(threeDs.url()).toMatch(tab);

  const served = await pageResponse;
  expect(served.status()).toBe(200);
  expect(served.headers()["cross-origin-opener-policy"]).toBe("same-origin");
  expect(served.headers()["cross-origin-embedder-policy"]).toBe("require-corp");
  expect(served.headers()["content-security-policy"]).toContain(
    "frame-ancestors 'none'",
  );
  expect((await download).status()).toBe(200);
  expect(await isolatedBrowser(threeDs)).toEqual({
    secure: true,
    isolated: true,
    sharedArrayBuffer: "function",
    webgl2: true,
  });

  // 3DS G4: the 4.3 copy's core and report, both local (4.3 appends ?v=… to the report). The synthetic dump can't
  // boot, so nothing past the downloads is asserted.
  await expect(threeDs.getByText("Start Game")).toBeVisible();
  const path = (r: { url(): string }) => new URL(r.url()).pathname;
  const report = threeDs.waitForResponse(
    (r) => path(r) === "/emulator/vendor/ejs-4.3/data/cores/reports/azahar.json",
  );
  const payload = threeDs.waitForResponse(
    (r) => path(r) === "/emulator/vendor/ejs-4.3/data/cores/azahar-thread-wasm.data",
  );
  await threeDs.getByText("Start Game").click();
  expect((await report).status()).toBe(200);
  expect((await payload).status()).toBe(200);
  await threeDs.close();
});

test("review queue: resolve with a suggestion", async () => {
  await page.goto("/review");
  await expectAccessible(page, "review queue");
  await page.getByRole("link", { name: "91-unmatched.gb" }).click();
  // It has suggestions, so that tab is open first (W2).
  await expect(page.getByRole("tab", { name: "Suggestions" })).toHaveAttribute(
    "aria-selected",
    "true",
  );
  await expectAccessible(page, "review detail");

  await page.getByRole("button", { name: "Use" }).first().click();
  // B7: the resolution runs as a job; the page shows it until the first poll finds it done.
  await expect(page.getByText("Resolving 91-unmatched.gb…")).toBeVisible();
  await expect(page.getByRole("button", { name: /Delete/ })).toBeDisabled();
  await expect(page).toHaveURL(/\/games\//);
  await expect(
    page.getByRole("article").getByText(/\(Unverified\)/),
  ).toBeVisible();
});

test("review queue: delete a file", async () => {
  await page.goto("/review");
  await expect(page.getByRole("link", { name: "91-unmatched.gb" })).toHaveCount(
    0,
  );
  await page.getByRole("button", { name: "Delete 93-mystery.gb" }).click();
  await expect(dialog()).toHaveAccessibleName("Delete File: 93-mystery.gb");
  await expect(dialog().getByText("93-mystery.gb").first()).toBeVisible();
  await expectAccessible(page, "review delete modal");
  await dialog().getByRole("button", { name: "Cancel" }).click();
  await expect(dialog()).toBeHidden();

  // Q2–Q4: the same file through the bulk delete.
  await page.getByRole("checkbox", { name: "Select 93-mystery.gb" }).check();
  await expect(page.getByText("1 selected")).toBeVisible();
  await page.getByRole("button", { name: "Delete selected (1)" }).click();
  await expect(dialog()).toHaveAccessibleName("Delete 1 review file?");
  await expect(dialog()).toContainText("93-mystery.gb");
  await expectAccessible(page, "review bulk delete modal");
  await dialog().getByRole("button", { name: "Delete", exact: true }).click();
  await expect(dialog()).toBeHidden();
  await expect(page.getByText("Deleted 1 file.")).toBeVisible();
  await expect(page.getByText("Nothing to review.")).toBeVisible();
});

test("images: upload, set cover, delete", async () => {
  await openGame("Star Courier");
  const viewport = page.viewportSize()!;
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.getByRole("button", { name: "Manage" }).click();
  await page.getByRole("button", { name: "View info.json" }).click();
  await expect(dialog()).toHaveAccessibleName("info.json");
  await expect(
    dialog().getByRole("button", { name: "Expand all" }),
  ).toBeVisible();
  await dialog().getByRole("button", { name: "Expand all" }).click();
  // J2: the JSON pane scrolls, the dialog doesn't.
  const fit = await page.evaluate(() => {
    const modal = document.querySelector(".modal.show")!;
    const pane = modal.querySelector(".border.rounded.overflow-auto")!;
    return {
      modal: modal.scrollHeight <= modal.clientHeight,
      overflow: getComputedStyle(pane).overflow,
    };
  });
  expect(fit).toEqual({ modal: true, overflow: "auto" });
  await expectAccessible(page, "info.json modal");
  await page.setViewportSize(viewport);
  await page.keyboard.press("Escape");
  await expect(dialog()).toBeHidden();
  await page.getByRole("tab", { name: /^Images \(/ }).click();
  await expect(page).toHaveURL(/tab=images/);
  const images = page.getByRole("region", { name: "Images" });
  const card = (fileName: string) =>
    images
      .locator(".card")
      .filter({
        has: page.getByRole("button", { name: `View ${fileName} full size` }),
      });
  await expect(images.getByText("Placeholder")).toBeVisible();

  // IGDB suggestions S17: the e2e stack has no IGDB keys, so the dialog explains what to set.
  await images.getByRole("button", { name: "Suggest Images" }).click();
  await expect(dialog()).toHaveAccessibleName("Suggest Images");
  await expect(
    dialog().getByText(
      "Image suggestions need an IGDB Client ID and secret. Set Sources:IgdbClientId and Sources:IgdbClientSecret and restart.",
    ),
  ).toBeVisible();
  await expectAccessible(page, "suggest images modal");
  await dialog().getByRole("button", { name: "Close", exact: true }).last().click();
  await expect(dialog()).toBeHidden();

  // A box art upload replaces the placeholder and takes over as cover.
  await images.getByRole("button", { name: "Upload Image" }).click();
  await expect(dialog()).toHaveAccessibleName("Upload Image");
  await expectAccessible(page, "upload image modal");
  // U6/I2: URL is the default source; a loopback URL is refused by the server; the fallback switches to the file input.
  await expect(
    dialog().getByRole("radio", { name: "URL", exact: true }),
  ).toBeChecked();
  await dialog().getByLabel("Image URL").fill("http://127.0.0.1/x.png");
  await dialog().getByRole("button", { name: "Upload", exact: true }).click();
  await expect(
    dialog().getByText("The URL points to a private or local network address."),
  ).toBeVisible();
  await expectAccessible(page, "upload image modal, URL error");
  await dialog().getByRole("button", { name: "Upload a file instead" }).click();
  await expect(dialog().getByLabel("Image URL")).toHaveCount(0);
  await dialog()
    .getByLabel("Upload an image")
    .setInputFiles({
      name: "art.png",
      mimeType: "image/png",
      buffer: png(64, [200, 40, 40]),
    });
  await dialog().getByRole("button", { name: "Upload", exact: true }).click();
  await expect(dialog()).toBeHidden();
  await expect(card("boxart-01.png").getByText("Cover")).toBeVisible();
  await expect(images.getByText("Placeholder")).toHaveCount(0);
  await expect(card("boxart-01.png")).toContainText("64×64");

  await images.getByRole("button", { name: "Upload Image" }).click();
  await dialog().getByText("File", { exact: true }).click();
  await dialog()
    .getByLabel("Upload an image")
    .setInputFiles({
      name: "shot.png",
      mimeType: "image/png",
      buffer: png(32, [40, 40, 200]),
    });
  await dialog().getByText("Screenshot", { exact: true }).click();
  await dialog().getByRole("button", { name: "Upload", exact: true }).click();
  await expect(dialog()).toBeHidden();
  await expect(card("snap-01.png").getByText("Screenshot")).toBeVisible();
  await expectAccessible(page, "game images");

  await card("snap-01.png")
    .getByRole("button", { name: "View snap-01.png full size" })
    .click();
  await expect(dialog()).toHaveAccessibleName("snap-01.png");
  await expectAccessible(page, "image view modal");
  await page.keyboard.press("Escape");
  await expect(dialog()).toBeHidden();

  await card("snap-01.png")
    .getByRole("button", { name: "Use as cover: snap-01.png" })
    .click();
  await expect(card("snap-01.png").getByText("Cover")).toBeVisible();
  await expect(
    page.getByRole("img", { name: "Star Courier cover" }),
  ).toHaveAttribute("width", "32");

  // Deleting the cover hands it back to the box art.
  await card("snap-01.png")
    .getByRole("button", { name: "Delete snap-01.png" })
    .click();
  await expect(dialog()).toHaveAccessibleName("Delete image");
  await expectAccessible(page, "delete image modal");
  await dialog().getByRole("button", { name: "Delete image" }).click();
  await expect(dialog()).toBeHidden();
  await expect(card("snap-01.png")).toHaveCount(0);
  await expect(card("boxart-01.png").getByText("Cover")).toBeVisible();
});

test("release notes: add, edit and delete", async () => {
  await openGame("Star Courier");
  const add = page.getByRole("button", { name: /^Add note for / }).first();
  const fileName = (await add.getAttribute("aria-label"))!.replace(
    "Add note for ",
    "",
  );

  // No note yet: the modal opens in Edit, View is disabled.
  await add.click();
  await expect(dialog()).toHaveAccessibleName(`Note — ${fileName}`);
  await expect(
    dialog().getByRole("radio", { name: "View", exact: true }),
  ).toBeDisabled();
  const text = dialog().getByLabel("Note (Markdown)");
  await text.fill("# Courier tips\n\nPress **Start** twice.");
  await expect(dialog().getByText(/^\d+ \/ 65,536 bytes$/)).toBeVisible();
  await dialog()
    .locator("label")
    .filter({ hasText: /^Preview$/ })
    .click();
  await expect(
    dialog().getByRole("heading", { name: "Courier tips" }),
  ).toBeVisible();
  await expectAccessible(page, "release note modal");
  await dialog().getByRole("button", { name: "Save" }).click();
  await expect(dialog()).toBeHidden();

  // Now it has one: View shows it rendered; edit it.
  await page
    .getByRole("button", { name: `Note for ${fileName}`, exact: true })
    .click();
  await expect(
    dialog().getByRole("heading", { name: "Courier tips" }),
  ).toBeVisible();
  await dialog()
    .locator("label")
    .filter({ hasText: /^Edit$/ })
    .click();
  await text.fill("## Updated tips");
  await dialog().getByRole("button", { name: "Save" }).click();
  await expect(dialog()).toBeHidden();
  await page.reload();
  await page
    .getByRole("button", { name: `Note for ${fileName}`, exact: true })
    .click();
  await expect(
    dialog().getByRole("heading", { name: "Updated tips" }),
  ).toBeVisible();

  // Delete it with the inline confirmation.
  await dialog().getByRole("button", { name: "Delete note" }).click();
  await expect(dialog().getByText("Delete this note?")).toBeVisible();
  await dialog().getByRole("button", { name: "Yes" }).click();
  await expect(dialog()).toBeHidden();
  await expect(
    page.getByRole("button", { name: `Add note for ${fileName}`, exact: true }),
  ).toBeVisible();
});

test("Admin: edit metadata, move a release, delete a release and a game", async () => {
  await openGame("Orbit Blocks");

  // T3–T6: the title is click-to-edit; Enter saves, and so does leaving the box (renamed back so later names hold).
  await page.getByRole("button", { name: "Orbit Blocks", exact: true }).click();
  await page.getByLabel("Title", { exact: true }).fill("Orbit Blocks Deluxe");
  await page.getByLabel("Title", { exact: true }).press("Enter");
  await expect(
    page.getByRole("heading", { level: 1, name: "Orbit Blocks Deluxe" }),
  ).toBeVisible();
  await expect(page.getByText("Saved.").first()).toBeVisible();
  await page.getByRole("button", { name: "Orbit Blocks Deluxe", exact: true }).click();
  await page.getByLabel("Title", { exact: true }).fill("Orbit Blocks");
  await page.getByLabel("Title", { exact: true }).blur();
  await expect(
    page.getByRole("heading", { level: 1, name: "Orbit Blocks", exact: true }),
  ).toBeVisible();

  // B3: the Admin actions sit in the Manage menu.
  await page.getByRole("button", { name: "Manage" }).click();
  await page.getByRole("button", { name: "Edit Metadata" }).click();
  await expect(dialog()).toBeVisible();
  await expectAccessible(page, "edit metadata modal");
  // IGDB lookup M1, M3: Details and Lookup Info tabs; e2e sets no IGDB keys, so Lookup Info only explains how to.
  await expect(dialog().getByRole("tab", { name: "Details" })).toHaveAttribute(
    "aria-selected",
    "true",
  );
  await dialog().getByRole("tab", { name: "Lookup Info" }).click();
  await expect(
    dialog().getByText("Metadata lookup needs an IGDB Client ID and secret."),
  ).toBeVisible();
  await expect(dialog().getByLabel("Search IGDB for")).toHaveCount(0);
  await dialog().getByRole("tab", { name: "Details" }).click();
  await expect(dialog().getByText("IGDB: not linked")).toBeVisible();
  // M2: Enter in an input saves.
  await dialog().getByLabel("Release year").fill("1995");
  await dialog().getByLabel("Release year").press("Enter");
  await expect(dialog()).toBeHidden();
  await expect(page.getByText("1995")).toBeVisible();

  // Split the Japanese release off into its own game (§11 Move release → new game); Move is in the release body.
  await page
    .getByRole("button", { name: "Show details for Orbit Blocks (Japan).gb" })
    .click();
  await page
    .getByRole("button", {
      name: "Move release Orbit Blocks (Japan).gb",
      exact: true,
    })
    .click();
  await dialog().getByLabel("A new game").check();
  await dialog().getByLabel("New game title").fill("Orbit Blocks Japan");
  await dialog().getByRole("button", { name: "Move release" }).click();
  await expect(dialog()).toBeHidden();
  await expect(
    page.getByRole("button", {
      name: "Move release Orbit Blocks (Japan).gb",
      exact: true,
    }),
  ).toHaveCount(0);

  // Its only release goes to _removed/, so the game goes too and we land on the Library.
  await openGame("Orbit Blocks Japan");
  // B9: Delete release sits in the expanded release body.
  await page
    .getByRole("button", { name: "Show details for Orbit Blocks (Japan).gb" })
    .click();
  await page
    .getByRole("button", {
      name: "Delete release Orbit Blocks (Japan).gb",
      exact: true,
    })
    .click();
  await dialog().getByRole("button", { name: "Delete" }).click();
  await expect(page).toHaveURL(/\/(\?[^/]*)?$/);
  await expect(page.locator("body")).not.toHaveClass(/modal-open/);
  await expect(page.locator(".modal-backdrop")).toHaveCount(0);
  // A shorter viewport guarantees overflow for the small seed library. A wheel must scroll after navigation.
  const viewport = page.viewportSize()!;
  await page.setViewportSize({ width: viewport.width, height: 400 });
  await page.mouse.move(400, 200);
  await page.mouse.wheel(0, 500);
  await expect
    .poll(() => page.evaluate(() => window.scrollY))
    .toBeGreaterThan(0);
  await page.setViewportSize(viewport);

  await openGame("Color Racer");
  await page.getByRole("button", { name: "Manage" }).click();
  await page.getByRole("button", { name: "Delete game" }).click();
  await dialog().getByRole("button", { name: "Delete" }).click();
  await expect(page).toHaveURL(/\/(\?[^/]*)?$/);
  await page
    .getByRole("searchbox", { name: "Search", exact: true })
    .fill("Color Racer");
  await expect(page.getByText("No games match these filters.")).toBeVisible();
});

test("Deleted ROMs: restore one release, delete the rest for good", async () => {
  await page.goto("/deleted");
  await expect(
    page.getByRole("heading", { level: 1, name: "Deleted ROMs" }),
  ).toBeVisible();
  await expect(
    page.getByRole("link", { name: /Deleted ROMs/ }).locator(".badge"),
  ).toBeVisible();
  await expect(
    page.getByText("Orbit Blocks (Japan).gb", { exact: true }),
  ).toBeVisible();
  await expectAccessible(page, "deleted ROMs");

  // D5: back to Ingestion and imported by a job (a just-deleted file may wait there as too recent).
  await page
    .getByRole("button", { name: "Restore Orbit Blocks (Japan).gb" })
    .click();
  await expect(page.getByText(/^Restored 1 file; importing\./)).toBeVisible();
  await page.getByRole("link", { name: "View job" }).click();
  await expect(page).toHaveURL(/\/jobs\//);
  await page.goto("/deleted");
  await expect(
    page.getByRole("heading", { level: 1, name: "Deleted ROMs" }),
  ).toBeVisible();
  await expect(
    page.getByText("Orbit Blocks (Japan).gb", { exact: true }),
  ).toHaveCount(0);

  // D4: the rest (Color Racer's ROMs) go permanently.
  await page.getByRole("button", { name: "Select all" }).click();
  await page.getByRole("button", { name: /^Delete selected/ }).click();
  await expect(dialog()).toHaveAccessibleName(
    /^Permanently delete \d+ files?\?$/,
  );
  await expectAccessible(page, "permanent delete modal");
  await dialog().getByRole("button", { name: "Delete", exact: true }).click();
  await expect(page.getByText("No deleted ROMs")).toBeVisible();
});

// Nintendo DS (spec Steps 10 and 11): the runnable cartridge is imported only now, after every test that asserts
// library-wide counts. scripts/test-e2e.ps1 staged it outside ingest/ and listed it in the seeded DS DAT.
// Colours from tests/RnRomManager.TestData/Fixtures/README.md (nds-marker): top screen, lower screen, lower after a press.
const NdsMarkerColours = {
  top: [33, 99, 197],
  lower: [197, 66, 33],
  touched: [66, 197, 66],
} as const;
type Rgb = readonly [number, number, number];
const nearColour = (actual: Rgb | undefined, expected: Rgb) =>
  !!actual && actual.every((v, i) => Math.abs(v - expected[i]) <= 24);

type GuestFrame = {
  image: [number, number];
  box: { x: number; y: number; w: number; h: number };
  top: Rgb;
  lower: Rgb;
};

/**
 * EmulatorJS letterboxes the guest frame inside its canvas: decode a canvas screenshot in a CSP-free page, take the
 * bounding box of pixels that aren't near-black (any channel above 16), and sample each screen (top and bottom half of
 * that box) a quarter of the way in from its top-left corner.
 */
async function guestFrame(
  decoder: Page,
  png: Buffer,
): Promise<GuestFrame | null> {
  return decoder.evaluate(async (b64) => {
    const img = new Image();
    img.src = `data:image/png;base64,${b64}`;
    await img.decode();
    const c = document.createElement("canvas");
    c.width = img.width;
    c.height = img.height;
    const ctx = c.getContext("2d")!;
    ctx.drawImage(img, 0, 0);
    const d = ctx.getImageData(0, 0, c.width, c.height).data;
    let minX = Infinity;
    let minY = Infinity;
    let maxX = -1;
    let maxY = -1;
    for (let y = 0; y < c.height; y++)
      for (let x = 0; x < c.width; x++) {
        const p = (y * c.width + x) * 4;
        if (d[p] > 16 || d[p + 1] > 16 || d[p + 2] > 16) {
          minX = Math.min(minX, x);
          maxX = Math.max(maxX, x);
          minY = Math.min(minY, y);
          maxY = Math.max(maxY, y);
        }
      }
    if (maxX < 0) return null;
    const w = maxX - minX + 1;
    const h = maxY - minY + 1;
    const at = (x: number, y: number): [number, number, number] => {
      const p = (Math.floor(y) * c.width + Math.floor(x)) * 4;
      return [d[p], d[p + 1], d[p + 2]];
    };
    return {
      image: [c.width, c.height] as [number, number],
      box: { x: minX, y: minY, w, h },
      top: at(minX + w / 4, minY + h / 8),
      lower: at(minX + w / 4, minY + h / 2 + h / 8),
    };
  }, png.toString("base64"));
}

/** Polls the guest frame until `ok` holds, within `timeout` ms; returns the last frame. */
async function waitForFrame(
  decoder: Page,
  canvas: ReturnType<Page["locator"]>,
  ok: (f: GuestFrame) => boolean,
  timeout: number,
): Promise<GuestFrame | null> {
  const until = Date.now() + timeout;
  let frame: GuestFrame | null = null;
  while (Date.now() < until) {
    frame = await guestFrame(decoder, await canvas.screenshot());
    if (frame && ok(frame)) return frame;
    await decoder.waitForTimeout(250);
  }
  return frame;
}

/** Polls GET /api/jobs/current (the Admin session) until no job holds the lock, within `seconds`. */
async function waitForIdleJobs(seconds = 30): Promise<void> {
  await expect
    .poll(async () => (await page.request.get("/api/jobs/current")).status(), {
      timeout: seconds * 1000,
    })
    .toBe(204);
}

test("Nintendo DS: the staged cartridge imports by DAT match and plays in DeSmuME with stylus and saves", async () => {
  // Allow for two cold emulator boots.
  test.setTimeout(180_000);
  const staged = process.env.E2E_STAGED_NDS;
  const ingest = process.env.E2E_INGEST;
  const base = process.env.E2E_BASE_URL;
  if (!staged || !ingest || !base)
    throw new Error(
      "E2E_STAGED_NDS, E2E_INGEST or E2E_BASE_URL is not set (run scripts/test-e2e.ps1).",
    );
  // The previous test's Restore started an import; a second start would be 409 JobRunning.
  await waitForIdleJobs();

  const name = "Marker Test (World).nds";
  const target = join(ingest, "nds", name);
  await copyFile(staged, target);
  const old = new Date(Date.now() - 60_000);
  await utimes(target, old, old);
  const xsrf =
    (await context.cookies()).find((c) => c.name === "XSRF-TOKEN")?.value ?? "";
  const start = await page.request.post("/api/jobs/ingest", {
    data: { files: [{ system: "nds", name }] },
    headers: { "X-XSRF-TOKEN": decodeURIComponent(xsrf) },
  });
  expect(start.status()).toBe(202);
  await waitForIdleJobs();

  await openGame("Marker Test");
  await expect(page.getByRole("article")).toHaveCount(1);
  await expect(page.getByRole("article", { name })).toBeVisible();
  const detailUrl = page.url();

  // A viewer who never played DS: a new context with the Admin session. Every off-origin request is aborted and noted.
  const ds = await context
    .browser()!
    .newContext({ storageState: await context.storageState() });
  const origin = new URL(base).origin;
  const offOrigin: string[] = [];
  await ds.route("**/*", (route) => {
    const url = route.request().url();
    if (new URL(url).origin === origin) return route.continue();
    offOrigin.push(url);
    return route.abort();
  });
  let requests: string[] = [];
  const responses = new Map<string, number>();
  ds.on("request", (r) => requests.push(r.url()));
  ds.on("response", (r) => responses.set(r.url(), r.status()));
  const viewer = await ds.newPage();
  const decoder = await ds.newPage();
  type SyncCallback = (error: Error | null) => void;
  type AutosaveWindow = Window & {
    EJS_emulator?: {
      saveSaveInterval: number;
      gameManager: {
        FS: { syncfs: (populate: boolean, callback: SyncCallback) => void };
      };
    };
    runNdsAutosave?: () => Promise<void>;
  };
  await viewer.addInitScript(() => {
    if (
      location.pathname !== "/emulator/play.html" ||
      new URLSearchParams(location.search).get("core") !== "desmume"
    ) {
      return;
    }
    const emulatorWindow = window as AutosaveWindow;
    const timers = new Map<number, () => void>();
    const setInterval = emulatorWindow.setInterval.bind(emulatorWindow);
    emulatorWindow.setInterval = (handler, delay, ...args) => {
      const id = setInterval(handler, delay, ...args);
      if (delay === 30_000 && typeof handler === "function")
        timers.set(id, () => handler(...args));
      return id;
    };
    // Run the real periodic callback on demand and wait for its IndexedDB flush.
    emulatorWindow.runNdsAutosave = async () => {
      const emulator = emulatorWindow.EJS_emulator;
      const tick = emulator && timers.get(emulator.saveSaveInterval);
      if (!emulator || !tick)
        throw new Error(
          "DeSmuME did not register its 30-second autosave timer",
        );
      const fs = emulator.gameManager.FS;
      const syncfs = fs.syncfs;
      await new Promise<void>((resolve, reject) => {
        let flushStarted = false;
        fs.syncfs = (populate, callback) => {
          if (populate !== false) return syncfs.call(fs, populate, callback);
          flushStarted = true;
          syncfs.call(fs, populate, (error) => {
            if (error) reject(error);
            else resolve();
            callback(error);
          });
        };
        try {
          tick();
          if (!flushStarted)
            throw new Error(
              "The DeSmuME autosave did not flush the filesystem",
            );
        } finally {
          fs.syncfs = syncfs;
        }
      });
    };
  });
  const settingsKey = `ejs-1-nds-${name}-settings`;
  const coreFiles = () =>
    requests
      .filter((u) => u.includes("/emulator/vendor/data/cores/"))
      .map((u) => u.split("/emulator/vendor/data/cores/")[1]);

  async function launch(): Promise<ReturnType<Page["locator"]>> {
    requests = [];
    await viewer.goto(detailUrl);
    const release = viewer.getByRole("article", { name });
    await release.getByRole("button", { name: `Play ${name}` }).click();
    await expect(viewer.getByRole("dialog")).toHaveAccessibleName(
      `Play ${name}`,
    );
    const frame = viewer.frameLocator("iframe");
    await frame.getByText("Start Game").click();
    return frame.locator("#game canvas").first();
  }

  function assertCoreRequests(): void {
    const files = coreFiles();
    expect(files[0]).toBe("reports/desmume.json");
    expect(
      responses.get(
        `${origin}/emulator/vendor/data/cores/reports/desmume.json`,
      ),
    ).toBe(200);
    expect(files.slice(1)).toHaveLength(1);
    expect(["desmume-wasm.data", "desmume-legacy-wasm.data"]).toContain(
      files[1],
    );
    expect(
      files.some((f) => f.includes("-thread") || f.includes("melonds")),
    ).toBe(false);
    expect(requests.some((u) => u.includes("/api/emulator/bios/"))).toBe(false);
    // EmulatorJS's localhost-only update check is the one off-origin request, and CSP blocks it anyway.
    expect(
      offOrigin.filter((u) => !u.endsWith("/stable/data/version.json")),
    ).toEqual([]);
  }

  // First launch: no saved settings, so the stylus defaults reach DeSmuME through EmulatorJS's runtime option update.
  await viewer.goto(detailUrl);
  expect(
    await viewer.evaluate((key) => localStorage.getItem(key), settingsKey),
  ).toBeNull();
  const romDownload = viewer.waitForResponse((r) =>
    /\/api\/games\/[^/]+\/roms\/\d+\/download$/.test(r.url()),
  );
  let canvas = await launch();
  expect((await romDownload).status()).toBe(200);
  const booted = await waitForFrame(
    decoder,
    canvas,
    (f) =>
      nearColour(f.top, NdsMarkerColours.top) &&
      nearColour(f.lower, NdsMarkerColours.lower),
    60_000,
  );
  expect(booted?.top).toBeDefined();
  expect(
    nearColour(booted!.top, NdsMarkerColours.top),
    `top ${booted!.top}`,
  ).toBe(true);
  expect(
    nearColour(booted!.lower, NdsMarkerColours.lower),
    `lower ${booted!.lower}`,
  ).toBe(true);
  assertCoreRequests();

  // A stylus press at the centre of the lower screen. The DS reads the touch screen once a frame, so the press is
  // held (a bare click is too short to be seen). The quarter point sampled is well clear of the press. On a slow
  // machine the first press can land before touch input is live (the stylus options arrive by a runtime update after
  // boot), so it is repeated until the lower screen changes; only the first press the cartridge sees has any effect.
  const rect = (await canvas.boundingBox())!;
  const scale = rect.width / booted!.image[0];
  await viewer.mouse.move(
    rect.x + (booted!.box.x + booted!.box.w / 2) * scale,
    rect.y + (booted!.box.y + (booted!.box.h * 3) / 4) * scale,
  );
  const pressUntil = Date.now() + 15_000;
  let touched: GuestFrame | null = null;
  let presses = 0;
  do {
    presses++;
    await viewer.mouse.down();
    await viewer.waitForTimeout(300);
    await viewer.mouse.up();
    touched = await waitForFrame(
      decoder,
      canvas,
      (f) => nearColour(f.lower, NdsMarkerColours.touched),
      Math.min(3_000, Math.max(0, pressUntil - Date.now())),
    );
  } while (
    !nearColour(touched?.lower, NdsMarkerColours.touched) &&
    Date.now() < pressUntil
  );
  expect(
    nearColour(touched?.lower, NdsMarkerColours.touched),
    `lower ${touched?.lower} after ${presses} press(es)`,
  ).toBe(true);
  if (presses > 1) console.log(`DS stylus: touched after ${presses} presses`);
  await canvas.evaluate(() => {
    const runAutosave = (window as AutosaveWindow).runNdsAutosave;
    if (!runAutosave)
      throw new Error("The DeSmuME autosave probe was not installed");
    return runAutosave();
  });
  await viewer
    .getByRole("dialog")
    .locator(".modal-footer")
    .getByRole("button", { name: "Close" })
    .click();
  await expect(viewer.getByRole("dialog")).toBeHidden();

  // Second launch: stale saved settings (melonDS, threads, external BIOS) are in the boot-time core options file.
  await viewer.evaluate(
    (key) =>
      localStorage.setItem(
        key,
        JSON.stringify({
          controlSettings: {},
          settings: {
            retroarch_core: "melonds",
            ejs_threads: "enabled",
            desmume_use_external_bios: "enabled",
          },
          cheats: [],
        }),
      ),
    settingsKey,
  );
  canvas = await launch();
  // The saved marker shows the third colour at boot, with no press: the save persisted, on the built-in BIOS.
  const reopened = await waitForFrame(
    decoder,
    canvas,
    (f) =>
      nearColour(f.top, NdsMarkerColours.top) &&
      nearColour(f.lower, NdsMarkerColours.touched),
    60_000,
  );
  expect(
    nearColour(reopened?.top, NdsMarkerColours.top),
    `top ${reopened?.top}`,
  ).toBe(true);
  expect(
    nearColour(reopened?.lower, NdsMarkerColours.touched),
    `lower ${reopened?.lower}`,
  ).toBe(true);
  assertCoreRequests();
  await ds.close();
});

test("ROM Zip Config: DS moves into its ZIP and back, the ROM downloading byte-exact each time", async () => {
  test.setTimeout(120_000);
  const staged = process.env.E2E_STAGED_NDS;
  if (!staged)
    throw new Error("E2E_STAGED_NDS is not set (run scripts/test-e2e.ps1).");
  const bytes = await readFile(staged);
  await waitForIdleJobs();

  const games = await (
    await page.request.get("/api/games?system=nds&q=Marker")
  ).json();
  const gameId: string = games.items[0].id;
  const downloadsExact = async () => {
    const detail = await (
      await page.request.get(`/api/games/${gameId}`)
    ).json();
    const download = await page.request.get(detail.roms[0].downloadUrl);
    expect(download.status()).toBe(200);
    expect(Buffer.compare(await download.body(), bytes)).toBe(0);
    return detail.roms[0].externalFileName as string | null;
  };

  const toggle = async (button: string, configured: string) => {
    await page.goto("/admin/rom-storage");
    const row = (name: string) =>
      page.getByRole("row").filter({ hasText: name });
    await expect(
      row("Game Boy Advance").getByRole("button", { name: /^Store (in|beside) ZIP$/ }),
    ).toHaveCount(0);
    await expectAccessible(page, "ROM Zip Config");
    await row("Nintendo DS").getByRole("button", { name: button }).click();
    await dialog().getByRole("button", { name: "Start moving" }).click();
    await expect(page.getByText("The storage job has started.")).toBeVisible();
    await waitForIdleJobs(90);

    const jobs = await (
      await page.request.get("/api/jobs?type=StorageMigration&pageSize=1")
    ).json();
    const job = jobs.items[0];
    expect(job.status).toBe("Completed");
    const items = await (
      await page.request.get(`/api/jobs/${job.id}/items`)
    ).json();
    expect(items.items.map((i: { outcome: string }) => i.outcome)).toEqual([
      "Migrated",
    ]);
    await page.goto("/jobs");
    await expect(
      page.getByRole("row").filter({ hasText: "Moving ROMs" }).first(),
    ).toContainText("Completed");
    await expectAccessible(page, "jobs");
    await page.goto("/admin/rom-storage");
    await expect(
      row("Nintendo DS")
        .getByRole("cell")
        .nth(3)
        .getByRole("img", { name: configured }),
    ).toBeVisible();
  };

  await toggle("Store in ZIP", "Stored in ZIP");
  expect(await downloadsExact()).toBeNull();

  // Back beside its ZIP, as later tests expect.
  await toggle("Store beside ZIP", "Beside ZIP");
  expect(await downloadsExact()).not.toBeNull();
});

test("ZIP compression: GBA repacks to Store and back, its ROM downloading byte-exact", async () => {
  test.setTimeout(120_000);
  await waitForIdleJobs();

  const romBytes = async () => {
    await openGame("Star Courier");
    const download = page.waitForEvent("download");
    await page.getByRole("button", { name: "Download ROM" }).click();
    return readFile(await (await download).path());
  };
  const before = await romBytes();

  const row = (name: string) => page.getByRole("row").filter({ hasText: name });
  const setLevel = async (value: string, label: string) => {
    await page.goto("/admin/rom-storage");
    await expect(
      page.getByRole("columnheader", { name: "ZIP compression" }),
    ).toBeVisible();
    await expect(
      page.getByRole("heading", { name: "ZIP compression levels" }),
    ).toBeVisible();
    await row("Game Boy Advance")
      .getByRole("button", { name: "Edit ZIP compression of Game Boy Advance" })
      .click();
    await dialog().getByLabel("Compression level").selectOption(value);
    await dialog().getByRole("button", { name: "Change and repack" }).click();
    await expect(page.getByText("Repacking Game Boy Advance ZIPs")).toBeVisible();
    await waitForIdleJobs(90);

    const jobs = await (
      await page.request.get("/api/jobs?type=ZipRepack&pageSize=1")
    ).json();
    const job = jobs.items[0];
    expect(job.status).toBe("Completed");
    const items = await (
      await page.request.get(`/api/jobs/${job.id}/items?pageSize=200`)
    ).json();
    expect(items.items.length).toBeGreaterThan(0);
    expect(
      new Set(items.items.map((i: { outcome: string }) => i.outcome)),
    ).toEqual(new Set(["Repacked"]));
    await page.goto("/jobs");
    await expect(
      page.getByRole("row").filter({ hasText: "Repacking ZIPs" }).first(),
    ).toContainText("Completed");
    await page.goto("/admin/rom-storage");
    await expect(row("Game Boy Advance")).toContainText(label);
  };

  await setLevel("Store", "Store");
  expect(Buffer.compare(await romBytes(), before)).toBe(0);

  // Back to the default, as later tests expect.
  await setLevel("Med", "Medium");
});

test("Missing Metadata report, and the phone navbar closes after a link", async () => {
  await page.goto("/reports/missing-metadata");
  await expect(page.getByRole("link", { name: "Missing Metadata" })).toHaveAttribute(
    "aria-current",
    "page",
  );
  const red = page.getByRole("row").filter({ hasText: "Red Horizon" });
  await expect(red).toBeVisible();
  await expect(red.getByText("Missing", { exact: true })).toHaveCount(3);
  await expect(red.getByRole("link")).toHaveAttribute(
    "href",
    /\/games\/[0-9a-f-]+\?from=missing-metadata$/,
  );
  await expectAccessible(page, "missing metadata report");

  const viewport = page.viewportSize()!;
  await page.setViewportSize({ width: 375, height: 812 });
  await page.getByRole("button", { name: "Toggle navigation" }).click();
  const menu = page.locator("#main-nav");
  await expect(menu).toHaveClass(/\bshow\b/);
  await menu.getByRole("link", { name: "Collections" }).click();
  await expect(page).toHaveURL(/\/collections$/);
  await expect(menu).not.toHaveClass(/\bshow\b/);
  await page.setViewportSize(viewport);
});

test("Missing IGDB IDs report", async () => {
  // IGDB lookup R2, R3: no game is linked in e2e, so the seeded ones are listed.
  await page.goto("/reports/missing-igdb-ids");
  await expect(page.getByRole("link", { name: "Missing IGDB IDs" })).toHaveAttribute(
    "aria-current",
    "page",
  );
  const red = page.getByRole("table").getByRole("link", { name: "Red Horizon", exact: true });
  // Not linked rows open the game as Next missing IGDB ID does.
  await expect(red).toHaveAttribute("href", /\/games\/[0-9a-f-]+\?from=missing-igdb$/);
  await expectAccessible(page, "missing igdb ids report");
  await red.click();
  await expect(page.getByRole("heading", { level: 1, name: "Red Horizon" })).toBeVisible();
  // The action row's button (the footer has one with the same name).
  await expect(
    page.getByRole("main").getByRole("button", { name: "Next missing IGDB ID" }),
  ).toBeVisible();
});

test("release editing, Games With Hacks, Newly Added and the phone shell", async () => {
  // B2–B8: Hack on a release no other flow names renames it to match.
  await openGame("Orbit Blocks");
  const name = "Orbit Blocks (World).gb";
  await page.getByRole("button", { name: `Show details for ${name}` }).click();
  await page.getByRole("button", { name: `Edit release ${name}`, exact: true }).click();
  await expect(dialog()).toHaveAccessibleName("Edit release");
  await expect(dialog().getByText("File name unchanged")).toBeVisible();
  await dialog().getByLabel("Hack", { exact: true }).check();
  await expect(dialog().getByText("Orbit Blocks (World) (Hack).gb", { exact: true })).toBeVisible();
  await expectAccessible(page, "edit release modal");
  await dialog().locator(".modal-footer").getByRole("button", { name: "Save" }).click();
  await expect(dialog()).toBeHidden();
  await expect(page.getByText("Release updated: Orbit Blocks (World) (Hack).gb")).toBeVisible();
  await expect(
    page.getByRole("article", { name: "Orbit Blocks (World) (Hack).gb" }),
  ).toBeVisible();

  // B13: the game is now in Games With Hacks.
  await page.goto("/reports/hacks");
  const hacked = page.getByRole("table").getByRole("link", { name: "Orbit Blocks", exact: true });
  await expect(hacked).toHaveAttribute("href", /\/games\/[0-9a-f-]+\?tab=releases$/);
  await expectAccessible(page, "games with hacks report");
  // F2: the report's System filter is the Games page's dropdown.
  await chooseFilter("System", "gb");
  await expect(page).toHaveURL(/\/reports\/hacks\?system=gb$/);
  await expect(hacked).toBeVisible();

  // Image Resolutions R5–R9: Star Courier's 64 × 64 box art (uploaded in "images: upload, set cover, delete") is listed,
  // and the Max width filter is remembered when the report is opened again without a query.
  await page.getByRole("link", { name: "Image Resolutions", exact: true }).click();
  await expect(page).toHaveURL(/\/reports\/image-resolutions$/);
  const courier = page.getByRole("table").getByRole("row").filter({ hasText: "Star Courier" });
  await expect(courier.first()).toContainText("64 × 64");
  await page.getByLabel("Max width").fill("100");
  await page.getByLabel("Max width").press("Enter");
  await expect(page).toHaveURL(/\/reports\/image-resolutions\?maxWidth=100$/);
  await expect(courier.first()).toBeVisible();
  await page.goto("/reports/image-resolutions");
  await expect(page).toHaveURL(/\/reports\/image-resolutions\?maxWidth=100$/);
  await expect(page.getByLabel("Max width")).toHaveValue("100");
  await expectAccessible(page, "image resolutions report");

  // B14, L4: Newly Added shows the seeded games, one tab per system.
  await page.getByRole("link", { name: "Newly Added", exact: true }).click();
  await expect(page).toHaveURL(/\/reports\/newly-added$/);
  await page.getByRole("tab", { name: "GB", exact: true }).click();
  await expect(page).toHaveURL(/\/reports\/newly-added\?system=gb$/);
  await expect(page.locator("#newly-added-panel").getByRole("link").first()).toBeVisible();
  await expectAccessible(page, "newly added report");

  // B29, B30: on a phone the footer's Admin buttons and the navbar search show without opening the menu.
  const viewport = page.viewportSize()!;
  await page.setViewportSize({ width: 375, height: 812 });
  await expect(page.getByRole("button", { name: "Next missing image" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Next missing IGDB ID" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Open the first review item" })).toBeVisible();
  await expect(page.locator("#main-nav")).not.toHaveClass(/\bshow\b/);
  await expect(
    page.locator("nav.navbar > .container > button[aria-label='Search']"),
  ).toBeVisible();
  await page.setViewportSize(viewport);
});

test("Admin: account types and the create-user modal", { tag: "@shard-b" }, async () => {
  await page.goto("/admin/users");
  // U6: the signed-in admin can't change their own type or disable themselves.
  await expect(page.getByLabel("Account type of admin")).toHaveValue("admin");
  await expect(page.getByLabel("Account type of admin")).toBeDisabled();
  await expect(page.getByRole("button", { name: "Disable admin" })).toBeDisabled();

  await page.getByRole("button", { name: "Create user" }).click();
  await expect(dialog()).toHaveAccessibleName("Create user");
  await expectAccessible(page, "create user modal");
  await dialog().getByLabel("Username").fill("editor");
  await dialog().getByLabel("Initial password").fill(`${newPassword}-editor`);
  await dialog().getByLabel("Account type").selectOption("admin");
  await dialog().getByRole("button", { name: "Create" }).click();
  await expect(dialog()).toBeHidden();
  await expect(page.getByLabel("Account type of editor")).toHaveValue("admin");
  await expect(page.getByLabel("Account type of editor")).toBeEnabled();
});

test("Admin: live logs", { tag: "@shard-b" }, async () => {
  await page.goto("/admin/logs");
  await expect(
    page.getByRole("link", { name: "Logs", exact: true }),
  ).toHaveAttribute("aria-current", "page");
  // The buffer already holds startup and request events.
  await expect(
    page.getByRole("log", { name: "Live log" }).locator(".badge").first(),
  ).toBeVisible();
  await expectAccessible(page, "logs");
});

test("keyboard shortcuts: use, rebind and use again", { tag: "@shard-b" }, async () => {
  await openLibrary();
  await expect(page.locator("nav.navbar")).toHaveClass(/sticky-top/);
  await expect(
    page.getByRole("button", { name: "Shortcuts", exact: true }),
  ).toBeVisible();

  // The default Alt+Shift+I goes to Ingestion (bindings load after sign-in, so retry until they have).
  await expect(async () => {
    await page.keyboard.press("Alt+Shift+KeyI");
    await expect(page).toHaveURL(/\/ingestion$/, { timeout: 1_000 });
  }).toPass();

  await page.getByRole("button", { name: "Shortcuts", exact: true }).click();
  await expect(dialog()).toHaveAccessibleName("Keyboard Shortcuts");
  await expect(
    dialog().getByRole("row", { name: /Go to Library/ }),
  ).toContainText("Alt+Shift+L");
  await expectAccessible(page, "keyboard shortcuts modal");

  await dialog().getByRole("button", { name: "Edit Go to Library" }).click();
  await expect(dialog().getByText("Press keys…")).toBeVisible();
  // Escape only stops capturing; the dialog stays open.
  await page.keyboard.press("Escape");
  await expect(dialog()).toBeVisible();
  await dialog().getByRole("button", { name: "Edit Go to Library" }).click();
  await page.keyboard.press("Alt+Shift+KeyK");
  await expect(
    dialog().getByRole("row", { name: /Go to Library/ }),
  ).toContainText("Alt+Shift+K");
  await dialog().getByRole("button", { name: "Save" }).click();
  await expect(dialog()).toBeHidden();

  // A full load fetches the saved bindings again: retry until they're in.
  await page.goto("/reports/library-size");
  await expect(async () => {
    await page.keyboard.press("Alt+Shift+KeyK");
    await expect(page).toHaveURL(/\/$/, { timeout: 1_000 });
  }).toPass();

  // Back to the defaults for anything after this.
  await page.getByRole("button", { name: "Shortcuts", exact: true }).click();
  await dialog().getByRole("button", { name: "Reset to defaults" }).click();
  await dialog().getByRole("button", { name: "Save" }).click();
  await expect(dialog()).toBeHidden();
});

test("Admin: database backups", { tag: "@shard-b" }, async () => {
  await page.goto("/admin/backups");
  await expect(
    page.getByRole("link", { name: "Backups", exact: true }),
  ).toHaveAttribute("aria-current", "page");
  // Release editing B34: Schedule and Status are closed accordion items; the list starts open.
  await page.getByRole("button", { name: "Schedule", exact: true }).click();
  await page.getByRole("button", { name: "Status", exact: true }).click();
  await expect(page.getByLabel(/^Daily at \(server time, /)).toHaveValue(
    "03:30",
  );
  // The default daily backup (03:30 server time) has already run when the suite straddles 03:30, so the list may
  // start with one file; "Back up now" must add a new one either way.
  const backupFiles = page.getByRole("cell", {
    name: /^rnrommanager-\d{8}-\d{6}\.zip$/,
  });
  const empty = page.getByText("No backups yet.");
  await expect(empty.or(backupFiles.first())).toBeVisible();
  const before = await backupFiles.allTextContents();
  if (before.length === 0) await expect(empty).toBeVisible();
  await expectAccessible(page, "backups");

  await page.getByRole("button", { name: /Back up now/ }).click();
  await expect
    .poll(
      async () =>
        (await backupFiles.allTextContents()).some((n) => !before.includes(n)),
      { timeout: 30_000 },
    )
    .toBe(true);
  await expect(page.getByTestId("last-result")).toContainText("rnrommanager-");
  await expectAccessible(page, "backups with a backup");
});

test("Admin and Integrity pages are accessible", { tag: "@shard-b" }, async () => {
  for (const [path, heading] of [
    ["/integrity", "Integrity"],
    ["/admin/users", "Admin"],
    ["/admin/dats", "Admin"],
    ["/admin/settings", "Admin"],
  ]) {
    await page.goto(path);
    // B1: the section title is visually hidden on a wide screen but stays the page's one h1.
    await expect(
      page.getByRole("heading", { level: 1, name: heading }),
    ).toBeAttached();
    await expect(page.locator('[role="status"]')).toHaveCount(0); // finished loading
    await expectAccessible(page, path);
  }
});

test("footer: About and the version, for everyone", { tag: "@shard-b" }, async () => {
  await page.goto("/");
  const footer = page.locator("footer.app-footer");
  await expect(footer).toBeVisible();
  await expect(
    footer.getByRole("link", { name: /^RnRomManager/ }),
  ).toHaveAttribute("href", "https://github.com/rniemand/RnRomManager/releases");
  await footer.getByRole("button", { name: "About" }).click();
  await expect(dialog()).toHaveAccessibleName("About RnRomManager");
  await expect(dialog().getByRole("cell", { name: "MIT" }).first()).toBeVisible();
  await expectAccessible(page, "about modal");
  await dialog().getByRole("button", { name: "Close" }).last().click();
  await expect(dialog()).toBeHidden();
});

// B26/B27: keep this the LAST test in the file. It replaces every table and signs everyone out, so nothing may run
// after it, even in a single-stack run where tags don't filter.
test("Admin: restore a backup from the Backups page", { tag: "@shard-b" }, async () => {
  await page.goto("/admin/backups");
  const backupFiles = page.getByRole("cell", {
    name: /^rnrommanager-\d{8}-\d{6}( \(\d+\))?\.zip$/,
  });
  const before = await backupFiles.allTextContents();
  // Release editing B34: Back up now is in the closed Status item.
  await page.getByRole("button", { name: "Status", exact: true }).click();
  await page.getByRole("button", { name: /Back up now/ }).click();
  await expect
    .poll(
      async () =>
        (await backupFiles.allTextContents()).find((n) => !before.includes(n)),
      { timeout: 30_000 },
    )
    .toBeTruthy();
  const created = (await backupFiles.allTextContents()).find(
    (n) => !before.includes(n),
  )!;

  await page.getByRole("button", { name: `Restore ${created}` }).click();
  await expect(dialog()).toHaveAccessibleName("Restore backup");
  await expect(dialog()).toContainText("Everyone, including you, is signed out.");
  await expectAccessible(page, "restore confirm");
  await dialog().getByRole("button", { name: "Restore", exact: true }).click();

  await expect(page).toHaveURL(/\/login/, { timeout: 120_000 });
  await page.getByLabel("Username").fill("admin");
  await page.getByLabel("Password").fill(newPassword);
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page).not.toHaveURL(/\/login/);

  await page.goto("/admin/backups");
  await expect(page.getByRole("cell", { name: created, exact: true })).toBeVisible();
  await expect(backupFiles).not.toHaveCount(before.length + 1); // the safety backup was added too
});
