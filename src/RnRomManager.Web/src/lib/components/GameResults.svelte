<script lang="ts">
	import type { Snippet } from 'svelte';
	import { resolve } from '$app/paths';
	import type { GameListItem } from '../api/types';
	import { formatCount } from '../format';
	import { tooltip } from '../tooltip';
	import { opensInNewTab, playUrl } from '../emulator';
	import EmulatorModal from './EmulatorModal.svelte';
	import FileSize from './FileSize.svelte';
	import Icon from './Icon.svelte';
	import RatingControl from './RatingControl.svelte';
	import SystemBadge from './SystemBadge.svelte';
	import { systemColour } from '../systemColours';

	/** The Library's game grid / list (spec §11), shared by the Library and a collection's page (B24). */
	interface Props {
		/** Bindable: the rating control updates each game in place. */
		games: GameListItem[];
		view: 'grid' | 'list';
		/** A per-game control (B24: Remove from collection), in the card's corner and a last table column. */
		action?: Snippet<[GameListItem]>;
		/** B26: given → a checkbox per game, ticking toggles its id in this set. */
		selected?: Set<string>;
	}

	let { games = $bindable(), view, action, selected }: Props = $props();

	function toggle(id: string, on: boolean): void {
		if (on) selected?.add(id);
		else selected?.delete(id);
	}

	/** C1: `1 update`, `2 updates`. */
	const updatesText = (n: number) => `${formatCount(n)} ${n === 1 ? 'update' : 'updates'}`;

	// C2: one modal for every card; the list item carries what the emulator needs, so nothing is fetched.
	let playing = $state<GameListItem | null>(null);
	let playOpen = $state(false);

	function play(game: GameListItem): void {
		playing = game;
		playOpen = true;
	}

	const canPlay = (game: GameListItem) => game.playRomId != null && !selected;
	const actionsColumn = $derived(!!action || games.some((g) => g.playRomId != null));
</script>

<!-- C2: Play, or (PSP B3) a link that opens the isolated player in its own tab. -->
{#snippet playAction(game: GameListItem, classes: string)}
	{#if game.browserCore && opensInNewTab(game.browserCore)}
		<!-- Static player page outside the SPA router. -->
		<!-- eslint-disable svelte/no-navigation-without-resolve -->
		<a
			class={classes}
			href={playUrl(
				game.id,
				{ id: game.playRomId!, fileName: game.playRomFileName ?? '' },
				game.browserCore
			)}
			target="_blank"
			rel="noopener"
			aria-label="Play {game.displayTitle} (opens in a new tab)"
			><!-- eslint-enable svelte/no-navigation-without-resolve --><Icon name="play" /></a
		>
	{:else}
		<button
			type="button"
			class={classes}
			aria-label="Play {game.displayTitle}"
			onclick={() => play(game)}><Icon name="play" /></button
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

{#if view === 'grid'}
	<div class="row row-cols-2 row-cols-md-3 row-cols-lg-4 row-cols-xl-6 g-3">
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
						{@render updatesBadge(game, 'position-absolute bottom-0 start-0 m-1')}
						{#if canPlay(game)}
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
						<div
							class="position-relative z-2 mt-1"
							class:d-flex={action}
							class:align-items-center={action}
							class:justify-content-between={action}
						>
							<RatingControl
								gameId={game.id}
								title={game.displayTitle}
								bind:rating={game.rating}
								bind:favourite={game.favourite}
							/>
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
							/>
						</td>
						<td class="text-end">{game.romCount}</td>
						<td class="text-end text-nowrap"><FileSize bytes={game.primarySizeBytes} /></td>
						{#if actionsColumn}
							<td class="text-end text-nowrap">
								{#if canPlay(game)}
									{@render playAction(game, 'btn btn-sm btn-outline-success')}
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

<EmulatorModal
	bind:open={playOpen}
	gameId={playing?.id ?? ''}
	core={playing?.browserCore ?? null}
	rom={playing?.playRomId != null
		? { id: playing.playRomId, fileName: playing.playRomFileName ?? '' }
		: null}
/>
