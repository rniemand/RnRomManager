<script lang="ts">
	import type { Snippet } from 'svelte';
	import { resolve } from '$app/paths';
	import { api } from '#lib/api/client.js';
	import type {
		GameDetail,
		GameListItem,
		RomDetail,
		SaveItem,
		PlaySave
	} from '#lib/types/index.js';
	import { formatCount } from '#lib/format.js';
	import { tooltip } from '#lib/tooltip.js';
	import { opensInNewTab, playLinkEvents, playUrl, usesPlayDisc } from '#lib/emulator.js';
	import { playableRom, savesApi } from '#lib/saves.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';
	import ConfirmModal from '#lib/components/modals/ConfirmModal.svelte';
	import EmulatorModal from '#lib/components/modals/EmulatorModal.svelte';
	import FileSize from '#lib/components/common/FileSize.svelte';
	import Icon from '#lib/icons/Icon.svelte';
	import { auth } from '#lib/stores/auth.svelte.js';
	import RatingControl from '#lib/components/forms/RatingControl.svelte';
	import SystemBadge from '#lib/components/common/SystemBadge.svelte';
	import { systemColour } from '#lib/systemColours.js';

	/** The Library's game grid / list (spec §11), shared by the Library and a collection's page (B24). */
	interface Props {
		/** Bindable: the rating control updates each game in place. */
		games: GameListItem[];
		view: 'grid' | 'list';
		/** A per-game control (B24: Remove from collection), in the card's corner and a last table column. */
		action?: Snippet<[GameListItem]>;
		/** B26: given → a checkbox per game, ticking toggles its id in this set. */
		selected?: Set<string>;
		/**
		 * B17: the grid sits in a two-thirds-wide pane (Collections' Publishers, Genres and Systems tabs), so it uses fewer
		 * columns and, from `sm` up, every card keeps room for the one-line rating (U6: below `sm` cards have no rating row).
		 */
		narrow?: boolean;
	}

	let { games = $bindable(), view, action, selected, narrow = false }: Props = $props();

	const gridColumns = $derived(
		narrow
			? 'row-cols-2 game-grid-xs row-cols-sm-2 row-cols-md-3 row-cols-xl-4'
			: 'row-cols-2 game-grid-xs row-cols-sm-2 row-cols-md-3 row-cols-lg-4 row-cols-xl-6'
	);

	function toggle(id: string, on: boolean): void {
		if (on) selected?.add(id);
		else selected?.delete(id);
	}

	/** C1: `1 update`, `2 updates`. */
	const updatesText = (n: number) => `${formatCount(n)} ${n === 1 ? 'update' : 'updates'}`;
	const releasesText = (n: number) => `${formatCount(n)} releases`;

	// C2: one modal for every card; the list item carries what the emulator needs, so nothing is fetched.
	// S4: picking up a save plays that save's release instead.
	type Playing = {
		game: GameListItem;
		rom?: Pick<RomDetail, 'id' | 'fileName'>;
		save?: PlaySave;
		disc?: boolean;
	};
	let playing = $state<Playing | null>(null);
	let playOpen = $state(false);

	function play(game: GameListItem): void {
		playing = { game };
		playOpen = true;
	}

	const canPlay = (game: GameListItem) => game.playRomId != null && !selected;
	/** S3: the viewer has a save here and the core plays in the Play dialog. */
	const canResume = (game: GameListItem) =>
		canPlay(game) &&
		game.ownSaveCount > 0 &&
		!!game.browserCore &&
		!opensInNewTab(game.browserCore);

	let resuming = $state<string | null>(null);
	let pendingBattery = $state<Playing | null>(null);
	let batteryOpen = $state(false);

	/** S4: the most recently updated own save that plays here, through the Saves tab's battery confirmation. */
	async function resume(item: GameListItem): Promise<void> {
		resuming = item.id;
		try {
			const [game, saves] = await Promise.all([
				api.get<GameDetail>(`/api/games/${item.id}`),
				savesApi.list(item.id)
			]);
			const me = auth.me?.username.toLowerCase();
			let found: { save: SaveItem; rom: RomDetail } | null = null;
			for (const save of saves) {
				if (!me || save.uploadedBy?.toLowerCase() !== me) continue;
				const rom = playableRom(game, save);
				if (rom) {
					found = { save, rom };
					break;
				}
			}
			if (!found) {
				toasts.show('No save to pick up here.', 'info');
				return;
			}
			const next: Playing = {
				game: item,
				rom: found.rom,
				save: { saveId: found.save.id, saveKind: found.save.kind },
				disc: usesPlayDisc(game.browserCore!, found.rom)
			};
			if (found.save.kind === 'battery') {
				pendingBattery = next;
				batteryOpen = true;
			} else {
				playing = next;
				playOpen = true;
			}
		} catch {
			toasts.show("Couldn't load your saves.", 'danger');
		} finally {
			resuming = null;
		}
	}

	function playBattery(): void {
		batteryOpen = false;
		playing = pendingBattery;
		playOpen = true;
	}
	const actionsColumn = $derived(!!action || games.some((g) => g.playRomId != null));
</script>

<!-- C2: Play, or (PSP B3) a link that opens the isolated player in its own tab. -->
{#snippet playAction(game: GameListItem, classes: string, style = '')}
	{#if game.browserCore && opensInNewTab(game.browserCore)}
		<!-- Static player page outside the SPA router. -->
		<!-- eslint-disable svelte/no-navigation-without-resolve -->
		<a
			class={classes}
			{style}
			href={playUrl(
				game.id,
				{ id: game.playRomId!, fileName: game.playRomFileName ?? '' },
				game.browserCore,
				game.playDisc
			)}
			target="_blank"
			rel="noopener"
			aria-label="Play {game.displayTitle} (opens in a new tab)"
			{...playLinkEvents(game.id, game.playRomId!)}
			><!-- eslint-enable svelte/no-navigation-without-resolve --><Icon name="play" /></a
		>
	{:else}
		<button
			type="button"
			class={classes}
			{style}
			aria-label="Play {game.displayTitle}"
			onclick={() => play(game)}><Icon name="play" /></button
		>
	{/if}
{/snippet}

<!-- S3: pick up where you left off: the viewer's latest playable save. -->
{#snippet resumeAction(game: GameListItem, classes: string)}
	<button
		type="button"
		class={classes}
		aria-label="Pick up where you left off in {game.displayTitle}"
		disabled={resuming === game.id}
		use:tooltip={'Pick up where you left off'}
		onclick={() => void resume(game)}
		>{#if resuming === game.id}<span class="spinner-border spinner-border-sm" aria-hidden="true"
			></span>{:else}<Icon name="play" />{/if}</button
	>
{/snippet}

<!-- Saves: opens the game on its Saves tab, to load a save and carry on. -->
{#snippet savesLink(game: GameListItem, classes: string, style = '')}
	{#if game.saveCount > 0}
		<a
			class={classes}
			{style}
			href="{resolve('/games/[id]', { id: game.id })}?tab=saves"
			aria-label="Saves of {game.displayTitle}"
			use:tooltip={`${formatCount(game.saveCount)} ${game.saveCount === 1 ? 'save' : 'saves'}`}
			><Icon name="save" /></a
		>
	{/if}
{/snippet}

{#snippet updatesBadge(game: GameListItem, extra: string)}
	{#if game.updateCount > 0}
		<span class="badge text-bg-info {extra}" use:tooltip={updatesText(game.updateCount)}>
			<span aria-hidden="true"><Icon name="update" /> {formatCount(game.updateCount)}</span>
			<span class="visually-hidden">{updatesText(game.updateCount)}</span>
		</span>
	{/if}
{/snippet}

<!-- DLC B7: as updatesBadge. -->
{#snippet dlcBadge(game: GameListItem, extra: string)}
	{#if game.dlcCount > 0}
		<span class="badge text-bg-info {extra}" use:tooltip={`${formatCount(game.dlcCount)} DLC`}>
			<span aria-hidden="true"><Icon name="dlc" /> {formatCount(game.dlcCount)}</span>
			<span class="visually-hidden">{formatCount(game.dlcCount)} DLC</span>
		</span>
	{/if}
{/snippet}

{#if view === 'grid'}
	<div class="row {gridColumns} g-3">
		{#each games as game (game.id)}
			<div class="col">
				<div class="card h-100 game-card">
					<div class="position-relative">
						<div class="card-img-top ratio ratio-1x1 bg-body-secondary overflow-hidden">
							{#if game.boxArtUrl}
								<img
									class="object-fit-cover w-100 h-100"
									src={game.boxArtUrl}
									alt="{game.displayTitle} box art"
									loading="lazy"
								/>
							{:else}
								<div class="d-flex align-items-center justify-content-center">
									<Icon name="system-{game.system}" class="fs-1" />
								</div>
							{/if}
						</div>
						<span
							class="badge text-bg-{systemColour(game.system)
								.name} position-absolute top-0 start-0 m-1"
						>
							<span aria-hidden="true">{game.system.toUpperCase()}</span>
							<span class="visually-hidden">{game.systemName}</span>
						</span>
						{#if game.primarySizeBytes != null}
							<span class="badge text-bg-light border position-absolute top-0 end-0 m-1">
								<FileSize bytes={game.primarySizeBytes} />
							</span>
						{/if}
						<div class="position-absolute bottom-0 start-0 m-1 d-flex gap-1">
							{#if game.romCount > 1}
								<span class="badge text-bg-secondary" use:tooltip={releasesText(game.romCount)}>
									<span aria-hidden="true"
										><Icon name="releases" /> {formatCount(game.romCount)}</span
									>
									<span class="visually-hidden">{releasesText(game.romCount)}</span>
								</span>
							{/if}
							{@render updatesBadge(game, '')}
							{@render dlcBadge(game, '')}
						</div>
						{@render savesLink(
							game,
							'btn btn-sm btn-secondary rounded-circle position-absolute end-0 m-1 z-2',
							// Above the Play button when there is one.
							canPlay(game) ? 'bottom: 2.25rem' : 'bottom: 0'
						)}
						{#if canResume(game)}
							{@render playAction(
								game,
								'btn btn-sm btn-primary rounded-circle position-absolute bottom-0 m-1 z-2',
								'right: 2.25rem'
							)}
							{@render resumeAction(
								game,
								'btn btn-sm btn-success rounded-circle position-absolute bottom-0 end-0 m-1 z-2'
							)}
						{:else if canPlay(game)}
							{@render playAction(
								game,
								'btn btn-sm btn-primary rounded-circle position-absolute bottom-0 end-0 m-1 z-2'
							)}
						{/if}
					</div>
					<div class="card-body p-2">
						{#if selected}
							<div class="form-check position-relative z-2 mb-1">
								<input
									class="form-check-input"
									type="checkbox"
									aria-label="Select {game.displayTitle}"
									checked={selected.has(game.id)}
									onchange={(e) => toggle(game.id, e.currentTarget.checked)}
								/>
							</div>
						{/if}
						<a
							class="d-block small fw-semibold text-truncate text-decoration-none stretched-link"
							href={resolve('/games/[id]', { id: game.id })}
							use:tooltip={game.displayTitle}>{game.displayTitle}</a
						>
						<div class="small text-body-secondary text-truncate">
							{[game.releaseYear, game.regions.join(', ')].filter(Boolean).join(' · ')}
						</div>
						<!-- U6: below sm a card is too narrow for the rating (about 148px); rating stays in list view and on the game page. -->
						<div
							class="position-relative z-2 mt-1"
							class:d-none={!action}
							class:d-sm-block={!action}
							class:d-flex={action}
							class:flex-wrap={action}
							class:align-items-center={action}
							class:justify-content-between={action}
						>
							<span class="d-none d-sm-inline"
								><RatingControl
									gameId={game.id}
									title={game.displayTitle}
									bind:rating={game.rating}
									bind:favourite={game.favourite}
									readonly={!auth.isAdmin}
								/></span
							>
							{#if action}{@render action(game)}{/if}
						</div>
					</div>
				</div>
			</div>
		{/each}
	</div>
{:else}
	<div class="table-responsive">
		<table class="table table-sm table-striped table-hover align-middle">
			<thead class="table-dark">
				<tr>
					{#if selected}<th scope="col"><span class="visually-hidden">Select</span></th>{/if}
					<th scope="col">Title</th>
					<th scope="col">System</th>
					<th scope="col">Year</th>
					<th scope="col">Regions</th>
					<th scope="col">Rating</th>
					<th scope="col" class="text-end">Releases</th>
					<th scope="col" class="text-end">Size</th>
					{#if actionsColumn}<th scope="col"><span class="visually-hidden">Actions</span></th>{/if}
				</tr>
			</thead>
			<tbody>
				{#each games as game (game.id)}
					<tr>
						{#if selected}
							<td>
								<input
									class="form-check-input"
									type="checkbox"
									aria-label="Select {game.displayTitle}"
									checked={selected.has(game.id)}
									onchange={(e) => toggle(game.id, e.currentTarget.checked)}
								/>
							</td>
						{/if}
						<td>
							<a href={resolve('/games/[id]', { id: game.id })}>{game.displayTitle}</a>
							{@render updatesBadge(game, 'ms-1')}
							{@render dlcBadge(game, 'ms-1')}
							{@render savesLink(game, 'ms-1 link-secondary')}
						</td>
						<td><SystemBadge system={game.system} name={game.systemName} /></td>
						<td>{game.releaseYear ?? ''}</td>
						<td>{game.regions.join(', ')}</td>
						<td>
							<RatingControl
								gameId={game.id}
								title={game.displayTitle}
								bind:rating={game.rating}
								bind:favourite={game.favourite}
								readonly={!auth.isAdmin}
							/>
						</td>
						<td class="text-end">{game.romCount}</td>
						<td class="text-end text-nowrap"><FileSize bytes={game.primarySizeBytes} /></td>
						{#if actionsColumn}
							<td class="text-end text-nowrap">
								{#if canPlay(game)}
									{@render playAction(game, 'btn btn-sm btn-outline-success')}
								{/if}
								{#if canResume(game)}
									{@render resumeAction(game, 'btn btn-sm btn-success rounded-circle ms-1')}
								{/if}
								{#if action}{@render action(game)}{/if}
							</td>
						{/if}
					</tr>
				{/each}
			</tbody>
		</table>
	</div>
{/if}

<ConfirmModal
	bind:open={batteryOpen}
	title="Load battery save"
	confirmLabel="Play"
	onconfirm={playBattery}
>
	<p class="mb-0">Loading this battery save replaces the in-browser save for this release.</p>
</ConfirmModal>

<EmulatorModal
	bind:open={playOpen}
	gameId={playing?.game.id ?? ''}
	core={playing?.game.browserCore ?? null}
	rom={playing?.rom ??
		(playing?.game.playRomId != null
			? { id: playing.game.playRomId, fileName: playing.game.playRomFileName ?? '' }
			: null)}
	disc={playing?.disc ?? playing?.game.playDisc ?? false}
	save={playing?.save ?? null}
/>
