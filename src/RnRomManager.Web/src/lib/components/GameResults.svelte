<script lang="ts">
	import { resolve } from '$app/paths';
	import type { GameListItem } from '../api/types';
	import { tooltip } from '../tooltip';
	import FileSize from './FileSize.svelte';
	import Icon from './Icon.svelte';
	import RatingControl from './RatingControl.svelte';

	/** The Library's game grid / list (spec §11), shared by the Library and a collection's page (B24). */
	interface Props {
		/** Bindable: the rating control updates each game in place. */
		games: GameListItem[];
		view: 'grid' | 'list';
	}

	let { games = $bindable(), view }: Props = $props();
</script>

{#if view === 'grid'}
	<div class="row row-cols-2 row-cols-md-3 row-cols-lg-4 row-cols-xl-6 g-3">
		{#each games as game (game.id)}
			<div class="col">
				<div class="card h-100">
					<div class="position-relative">
						<div class="card-img-top ratio ratio-1x1 bg-body-secondary">
							{#if game.boxArtUrl}
								<img
									class="object-fit-contain"
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
						<span class="badge text-bg-dark position-absolute top-0 start-0 m-1">
							<span aria-hidden="true">{game.system.toUpperCase()}</span>
							<span class="visually-hidden">{game.systemName}</span>
						</span>
						{#if game.primarySizeBytes != null}
							<span class="badge text-bg-light border position-absolute top-0 end-0 m-1">
								<FileSize bytes={game.primarySizeBytes} />
							</span>
						{/if}
					</div>
					<div class="card-body p-2">
						<a
							class="d-block small fw-semibold text-truncate text-decoration-none stretched-link"
							href={resolve('/games/[id]', { id: game.id })}
							use:tooltip={game.displayTitle}>{game.displayTitle}</a
						>
						<div class="small text-body-secondary text-truncate">
							{[game.releaseYear, game.regions.join(', ')].filter(Boolean).join(' · ')}
						</div>
						<div class="position-relative z-2 mt-1">
							<RatingControl
								gameId={game.id}
								title={game.displayTitle}
								bind:rating={game.rating}
								bind:favourite={game.favourite}
							/>
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
					<th scope="col">Title</th>
					<th scope="col">System</th>
					<th scope="col">Year</th>
					<th scope="col">Regions</th>
					<th scope="col">Rating</th>
					<th scope="col" class="text-end">Releases</th>
					<th scope="col" class="text-end">Size</th>
				</tr>
			</thead>
			<tbody>
				{#each games as game (game.id)}
					<tr>
						<td><a href={resolve('/games/[id]', { id: game.id })}>{game.displayTitle}</a></td>
						<td><span class="badge text-bg-dark">{game.systemName}</span></td>
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
					</tr>
				{/each}
			</tbody>
		</table>
	</div>
{/if}
