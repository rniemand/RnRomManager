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
 * modal visited. The seeded admin has both roles (`admin` + `rom-manager`).
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

test("login + forced password change", async () => {
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
});

test("ingestion of the seeded Basic set", async () => {
  await page.getByRole("link", { name: "Go to Ingestion" }).click();
  await expect(
    page.getByRole("cell", { name: "91-unmatched.gb", exact: true }),
  ).toBeVisible();
  await expectAccessible(page, "ingestion");

  // I1: Pending / Failed / Last runs are tabs under the always-visible Run section.
  await expect(
    page.getByRole("tab", { name: /^Pending files \(\d+\)$/ }),
  ).toHaveAttribute("aria-selected", "true");
  const ingest = process.env.E2E_INGEST;
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

  await page.getByRole("tab", { name: "Last runs" }).click();
  await expect(page).toHaveURL(/\/ingestion\?tab=runs$/);
  await expect(page.getByRole("button", { name: "Import all" })).toBeVisible();
  const run = page.getByRole("row").filter({ hasText: "Completed" });
  await expect(run).toContainText(
    "Ingested 38 · Duplicate 1 · Review 2 · Failed 1",
    {
      timeout: 60_000,
    },
  );

  await run.getByRole("link").click();
  await expect(page.getByRole("heading", { name: /Ingest job/ })).toBeVisible();
  await expect(
    page.getByRole("cell", { name: /92-invalid\.gb/ }),
  ).toBeVisible();
  await expectAccessible(page, "job detail");

  await page.goto("/ingestion?tab=runs");
  await expect(page.getByRole("tab", { name: "Last runs" })).toHaveAttribute(
    "aria-selected",
    "true",
  );
  await expect(
    page.getByRole("row").filter({ hasText: "Completed" }),
  ).toBeVisible();
  await expectAccessible(page, "ingestion last runs tab");

  // D4/D5: the invalid file is listed under Failed files with its reason, and deleting it goes through the modal.
  await page.goto("/ingestion");
  await page.getByRole("tab", { name: "Failed files (1)" }).click();
  await expect(page).toHaveURL(/\/ingestion\?tab=failed$/);
  await expect(page.getByRole("button", { name: "Import all" })).toBeVisible();
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

test("reports and the ingest audit trail", async () => {
  await page.getByRole("link", { name: "Reports", exact: true }).click();
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

  await page.getByRole("link", { name: "Admin", exact: true }).click();
  await page.getByRole("link", { name: "Auditing", exact: true }).click();
  const ingested = page
    .getByRole("row")
    .filter({ has: page.getByRole("cell", { name: "Ingested", exact: true }) });
  // One Ingested event per release: a bin/cue set is one event (B7a).
  await expect(ingested).toHaveCount(34);
  await expect(
    page
      .getByRole("row")
      .filter({
        has: page.getByRole("cell", { name: "IngestFileDeleted", exact: true }),
      }),
  ).toContainText("ingest/gb/_failed/92-invalid.gb");
  await expect(ingested.first()).toContainText("admin");
  await expect(ingested.first()).toContainText("ingest/");
  await expectAccessible(page, "audit trail");
});

test("browse, search and filter the library", async () => {
  await openLibrary();
  await expect(page.getByText("29 games")).toBeVisible();
  await expectAccessible(page, "library grid");
  // S1/L3: systems in display-name order after "All systems"; F3/L2: each with its game count, right-aligned.
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
    "3do",
    "a2600",
    "a5200",
    "a7800",
    "jaguar",
    "lynx",
    "coleco",
    "c64",
    "pet",
    "gb",
    "gba",
    "gbc",
    "gg",
    "gc",
    "sms",
    "md",
    "n64",
    "nds",
    "nes",
    "switch",
    "psx",
    "psp",
    "saturn",
    "segacd",
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
  await page.getByRole("link", { name: "← Library" }).click();
  await expect(page).toHaveURL(/system=gb/);
  await expect(page.getByText("3 games", { exact: true })).toBeVisible();
  // B1 (library-reset-missing-images): Reset is in the filter row, usable on a 320px phone and on a desktop.
  const filterRow = page.getByRole("search");
  await expect(
    filterRow.getByRole("button", { name: "Reset filters" }),
  ).toBeVisible();
  const viewport = page.viewportSize()!;
  for (const width of [320, 1280]) {
    await page.setViewportSize({ width, height: 800 });
    for (const name of ["System: Game Boy", "Reset filters"]) {
      const control = filterRow.getByRole("button", {
        name,
        exact: name === "Reset filters",
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
  await page.getByRole("button", { name: "Reset filters" }).click();
  await expect(page.getByText("29 games")).toBeVisible();
  await expect(page.getByRole("button", { name: "Reset filters" })).toHaveCount(
    0,
  );

  // The card grid fits a 320px phone: no sideways page scroll, and each card's Favourite button is on screen.
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
  const favouritesOffScreen = await page.evaluate(
    () =>
      [
        ...document.querySelectorAll(
          '.game-card button[aria-label="Favourite"]',
        ),
      ].filter((b) => {
        const box = b.getBoundingClientRect();
        return box.left < 0 || box.right > window.innerWidth;
      }).length,
  );
  expect(favouritesOffScreen).toBe(0);
  await page.setViewportSize(viewport);
});

test("game detail + downloads + ROM playback progress", async () => {
  await openGame("Orbit Blocks");
  await expect(page.getByText("Primary")).toBeVisible();
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
  await dialog()
    .locator(".modal-footer")
    .getByRole("button", { name: "Close" })
    .click();
  await expect(dialog()).toBeHidden();
  await expect(page.locator("iframe")).toHaveCount(0);

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
  await expect(page.locator("iframe")).toHaveCount(0);

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
  await page.getByRole("link", { name: "Admin", exact: true }).click();
  await page.getByRole("link", { name: "Auditing", exact: true }).click();
  for (const action of ["SaveAdded", "SaveUpdated", "SaveDeleted"])
    await expect(
      page
        .getByRole("row")
        .filter({ has: page.getByRole("cell", { name: action, exact: true }) })
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

  // G7: base and DLC are each stored next to the ZIP; the two updates have their own tab (game updates B9).
  await expect(page.getByRole("tab", { name: "Releases (2)" })).toHaveAttribute(
    "aria-selected",
    "true",
  );
  await expect(page.getByText("External", { exact: true })).toHaveCount(2);
  await expectAccessible(page, "Switch game detail");

  // G4: the primary (base) release first; the download is the original file, byte for byte. No updates (B10).
  await page.getByRole("button", { name: "Download ROM" }).click();
  const items = page.locator(".dropdown-menu.show .dropdown-item");
  await expect(items).toHaveCount(2);
  const download = page.waitForEvent("download");
  await items.first().click();
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
  const rows = page.locator("#releases-panel table tbody tr");
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
  await expect(dialog()).toBeHidden();
  await expect(page.locator("iframe")).toHaveCount(0);
}

test("Play: NES Quick Play from the library uses the local FCEUmm core", async () => {
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

test("Play: a PlayStation set streams its disc ZIP from the detail page and the library", async () => {
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

test("Play: a Saturn set asks for saturn_bios.bin, streams its disc and fetches the vendored Yabause core", async () => {
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

/** PSP B3: what PPSSPP needs, as the isolated tab actually sees it. */
async function pspBrowser(psp: Page) {
  return psp.evaluate(() => ({
    secure: isSecureContext,
    isolated: crossOriginIsolated,
    sharedArrayBuffer: typeof SharedArrayBuffer,
    webgl2: document.createElement("canvas").getContext("webgl2") !== null,
  }));
}

test("Play: a PSP UMD image opens the isolated player in a new tab from the detail page and the library", async () => {
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
  expect(await pspBrowser(psp)).toEqual({
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
  expect((await pspBrowser(fromLibrary)).isolated).toBe(true);
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

test("Play: Genesis explains its missing core; Master System and Game Gear start with SMS Plus GX", async () => {
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

test("Play: 32X explains its missing PicoDrive core", async () => {
  // 32X B3: the e2e image has no Emulator:PicodriveCorePath, so the HEAD check stops before the ROM or loader.
  const later: string[] = [];
  let checked = false;
  const track = (r: { url: () => string }) => {
    if (
      checked &&
      (r.url().includes("/api/emulator/cores/") ||
        r.url().includes("/roms/") ||
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

test("Play: SNES explains its missing Snes9x core from the detail page", async () => {
  // SNES B3: the e2e image has no Emulator:Snes9xCorePath, so the HEAD check stops before the ROM or loader.
  const later: string[] = [];
  let checked = false;
  const track = (r: { url: () => string }) => {
    if (
      checked &&
      (r.url().includes("/api/emulator/") ||
        r.url().includes("/roms/") ||
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

test("Play: SNES Quick Play from the library loads the release and the player once the core check passes", async () => {
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

test("Play: Atari 7800, Jaguar and Lynx launch with their bundled cores", async () => {
  await launchBundled("Canyon Glide", "Canyon Glide (USA).a78", "prosystem");
  await launchBundled(
    "Cobalt Drift",
    "Cobalt Drift (World).j64",
    "virtualjaguar",
  );
  await launchBundled("Prism Racer", "Prism Racer (USA, Europe).lnx", "handy");
});

test("global search, ratings and favourites", async () => {
  // Ctrl+K from the library opens the search; part of a title finds it; Enter opens it.
  await openLibrary();
  await expect(page.getByText("29 games")).toBeVisible();
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

test("collections: create, add from game page and bulk select, search, remove, delete", async () => {
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
  await expect(page.getByText("29 games")).toBeVisible();
});

test("collections discovery: browse publishers and genres across systems, share a selection", async () => {
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

test("collections systems: browse a system's games and its emulator links", async () => {
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
  await expect(dialog()).toHaveAccessibleName("Game Boy emulators");
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

test("collections discovery: two signed-in users share, replace and remove a publisher logo", async () => {
  // A plain user (no roles) next to the admin, in a browser of their own.
  const curatorPassword = `${newPassword}-curator`;
  await page.goto("/admin/users");
  await page.getByRole("button", { name: "Create user" }).click();
  await dialog().getByLabel("Username").fill("curator");
  await dialog()
    .getByLabel("Initial password")
    .fill(`${curatorPassword}-initial`);
  await dialog().getByRole("button", { name: "Create" }).click();
  await expect(dialog()).toBeHidden();

  const curatorContext = await context.browser()!.newContext();
  const curator = await curatorContext.newPage();
  try {
    await curator.goto("/login");
    await curator.getByLabel("Username").fill("curator");
    await curator.getByLabel("Password").fill(`${curatorPassword}-initial`);
    await curator.getByRole("button", { name: "Sign in" }).click();
    await expect(curator).toHaveURL(/\/change-password$/);
    await curator
      .getByLabel("Current password")
      .fill(`${curatorPassword}-initial`);
    await curator
      .getByLabel("New password", { exact: true })
      .fill(curatorPassword);
    await curator.getByLabel("Confirm new password").fill(curatorPassword);
    await curator.getByRole("button", { name: "Change password" }).click();
    await expect(curator).not.toHaveURL(/\/change-password$/);

    const logoOf = (p: Page) =>
      p
        .getByRole("list", { name: "Publishers" })
        .getByRole("link", { name: /Fixture Works/ })
        .locator("img");

    // B5: the admin adds the shared logo; Fixture Works has no starter art, so it showed text only.
    await page.goto("/collections?tab=publishers");
    await expect(logoOf(page)).toHaveCount(0);
    await page
      .getByRole("button", { name: "Change logo for Fixture Works" })
      .click();
    await expect(dialog()).toHaveAccessibleName("Logo for Fixture Works");
    await expect(
      dialog().getByText("No logo: the name shows as text."),
    ).toBeVisible();
    await expectAccessible(page, "publisher logo modal");
    await dialog()
      .getByLabel("Logo image file")
      .setInputFiles({
        name: "logo.png",
        mimeType: "image/png",
        buffer: png(48, [200, 60, 40]),
      });
    await dialog().getByRole("button", { name: "Save logo" }).click();
    await expect(dialog()).toBeHidden();
    await expect(logoOf(page)).toHaveAttribute(
      "src",
      /\/api\/discovery\/publishers\/logo\?key=[0-9a-f]{64}&v=\d+$/,
    );
    const adminVersion = await logoOf(page).getAttribute("src");
    await expect
      .poll(() =>
        logoOf(page).evaluate((img: HTMLImageElement) => img.naturalWidth),
      )
      .toBe(48);

    // Shared: the curator sees it, then replaces it; the last writer wins for everyone.
    await curator.goto("/collections?tab=publishers");
    await expect(logoOf(curator)).toHaveAttribute("src", adminVersion!);
    await curator
      .getByRole("button", { name: "Change logo for Fixture Works" })
      .click();
    const curatorDialog = curator.getByRole("dialog");
    await expect(
      curatorDialog.getByText("Shared logo, set by a user."),
    ).toBeVisible();
    await curatorDialog
      .getByLabel("Logo image file")
      .setInputFiles({
        name: "logo.png",
        mimeType: "image/png",
        buffer: png(64, [40, 160, 90]),
      });
    await curatorDialog.getByRole("button", { name: "Save logo" }).click();
    await expect(curatorDialog).toBeHidden();
    await expect(logoOf(curator)).not.toHaveAttribute("src", adminVersion!);
    const curatorVersion = await logoOf(curator).getAttribute("src");

    await page.reload();
    await expect(logoOf(page)).toHaveAttribute("src", curatorVersion!);
    await expect
      .poll(() =>
        logoOf(page).evaluate((img: HTMLImageElement) => img.naturalWidth),
      )
      .toBe(64);

    // Either user removes it; the text fallback shows again for both.
    await page
      .getByRole("button", { name: "Change logo for Fixture Works" })
      .click();
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

  // A box art upload replaces the placeholder and takes over as cover.
  await images.getByRole("button", { name: "Upload image" }).click();
  await expect(dialog()).toHaveAccessibleName("Upload image");
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

  await images.getByRole("button", { name: "Upload image" }).click();
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

  await page.getByRole("button", { name: "Edit metadata" }).click();
  await expect(dialog()).toBeVisible();
  await expectAccessible(page, "edit metadata modal");
  await dialog().getByLabel("Release year").fill("1995");
  await dialog().getByRole("button", { name: "Edit metadata" }).click();
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
  await page
    .getByRole("button", {
      name: "Delete Orbit Blocks (Japan).gb",
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
  // held (a bare click is too short to be seen). The quarter point sampled is well clear of the press.
  const rect = (await canvas.boundingBox())!;
  const scale = rect.width / booted!.image[0];
  await viewer.mouse.move(
    rect.x + (booted!.box.x + booted!.box.w / 2) * scale,
    rect.y + (booted!.box.y + (booted!.box.h * 3) / 4) * scale,
  );
  await viewer.mouse.down();
  await viewer.waitForTimeout(300);
  await viewer.mouse.up();
  const touched = await waitForFrame(
    decoder,
    canvas,
    (f) => nearColour(f.lower, NdsMarkerColours.touched),
    15_000,
  );
  expect(
    nearColour(touched?.lower, NdsMarkerColours.touched),
    `lower ${touched?.lower}`,
  ).toBe(true);
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

test("Admin: roles and the create-user modal", async () => {
  await page.goto("/admin/users");
  await expect(page.getByLabel("admin role of admin")).toBeChecked();
  await expect(page.getByLabel("rom-manager role of admin")).toBeChecked();

  await page.getByRole("button", { name: "Create user" }).click();
  await expect(dialog()).toHaveAccessibleName("Create user");
  await expectAccessible(page, "create user modal");
  await dialog().getByLabel("Username").fill("editor");
  await dialog().getByLabel("Initial password").fill(`${newPassword}-editor`);
  await dialog().getByLabel("rom-manager").check();
  await dialog().getByRole("button", { name: "Create" }).click();
  await expect(dialog()).toBeHidden();
  await expect(page.getByLabel("rom-manager role of editor")).toBeChecked();
  await expect(page.getByLabel("admin role of editor")).not.toBeChecked();
});

test("Admin: live logs", async () => {
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

test("keyboard shortcuts: use, rebind and use again", async () => {
  await openLibrary();
  await expect(page.locator("nav.navbar")).toHaveClass(/sticky-top/);
  await expect(
    page.getByRole("button", { name: "Keyboard shortcuts" }),
  ).toBeVisible();

  // The default Alt+Shift+I goes to Ingestion (bindings load after sign-in, so retry until they have).
  await expect(async () => {
    await page.keyboard.press("Alt+Shift+KeyI");
    await expect(page).toHaveURL(/\/ingestion$/, { timeout: 1_000 });
  }).toPass();

  await page.getByRole("button", { name: "Keyboard shortcuts" }).click();
  await expect(dialog()).toHaveAccessibleName("Keyboard shortcuts");
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
  await page.getByRole("button", { name: "Keyboard shortcuts" }).click();
  await dialog().getByRole("button", { name: "Reset to defaults" }).click();
  await dialog().getByRole("button", { name: "Save" }).click();
  await expect(dialog()).toBeHidden();
});

test("Admin: database backups", async () => {
  await page.goto("/admin/backups");
  await expect(
    page.getByRole("link", { name: "Backups", exact: true }),
  ).toHaveAttribute("aria-current", "page");
  await expect(page.getByLabel(/^Daily at \(server time, /)).toHaveValue(
    "03:30",
  );
  await expect(page.getByText("No backups yet.")).toBeVisible();
  await expectAccessible(page, "backups");

  await page.getByRole("button", { name: /Back up now/ }).click();
  await expect(
    page.getByRole("cell", { name: /^rnrommanager-\d{8}-\d{6}\.zip$/ }),
  ).toBeVisible({
    timeout: 30_000,
  });
  await expect(page.getByTestId("last-result")).toContainText("rnrommanager-");
  await expectAccessible(page, "backups with a backup");
});

test("Admin and Integrity pages are accessible", async () => {
  for (const [path, heading] of [
    ["/integrity", "Integrity"],
    ["/admin/users", "Admin"],
    ["/admin/dats", "Admin"],
    ["/admin/settings", "Admin"],
    ["/admin/about", "Admin"],
  ]) {
    await page.goto(path);
    await expect(
      page.getByRole("heading", { level: 1, name: heading }),
    ).toBeVisible();
    await expect(page.locator('[role="status"]')).toHaveCount(0); // finished loading
    await expectAccessible(page, path);
  }
});
