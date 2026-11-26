<script lang="ts">
	import type { About } from '#lib/types/index.js';
	import ConfirmModal from './ConfirmModal.svelte';
	import ErrorAlert from './ErrorAlert.svelte';
	import Icon from '#lib/icons/Icon.svelte';
	import EmptyState from './EmptyState.svelte';
	import Loading from './Loading.svelte';

	/** B12: the version, GitHub link, attribution and licences (what the Admin About tab used to show), for everyone. */
	interface Props {
		open: boolean;
		/** `GET /api/about`, loaded once by the footer; null while loading or after a failure. */
		about: About | null;
		error?: unknown;
		onretry?: () => void;
	}

	let { open = $bindable(), about, error = null, onretry }: Props = $props();

	const repoUrl = 'https://github.com/rniemand/RnRomManager';

	/** B25: filters the licences by name or licence (case-insensitive contains). */
	let search = $state('');
	const licenses = $derived.by(() => {
		const needle = search.trim().toLowerCase();
		const all = about?.licenses ?? [];
		return needle
			? all.filter(
					(l) => l.name.toLowerCase().includes(needle) || l.license.toLowerCase().includes(needle)
				)
			: all;
	});
</script>

<ConfirmModal bind:open title="About RnRomManager" size="lg">
	{#if error}
		<ErrorAlert {error} {onretry} />
	{:else if !about}
		<Loading />
	{:else}
		<p class="fs-5 mb-1">RnRomManager <span class="text-body-secondary">{about.version}</span></p>
		<p>
			<a href={repoUrl} rel="noopener noreferrer" target="_blank">
				<Icon name="github" /> rniemand/RnRomManager on GitHub
			</a>
		</p>

		<h2 class="h5 mt-4">Attribution</h2>
		<p>
			Game metadata comes from
			<a
				href="https://github.com/libretro/libretro-database"
				rel="noopener noreferrer"
				target="_blank">libretro-database</a
			>, licensed
			<a
				href="https://creativecommons.org/licenses/by-sa/4.0/"
				rel="noopener noreferrer"
				target="_blank">CC BY-SA 4.0</a
			>. Box art and screenshots come from libretro-thumbnails. They remain the copyright of their
			owners and are stored for personal use only.
		</p>

		<h2 class="h5 mt-4">Licences</h2>
		<label class="visually-hidden" for="about-licence-search">Search licences</label>
		<input
			id="about-licence-search"
			type="search"
			class="form-control form-control-sm mb-2"
			placeholder="Search licences"
			bind:value={search}
		/>
		{#if licenses.length === 0}
			<EmptyState message="No licences match" />
		{/if}
		<div class="card" hidden={licenses.length === 0}>
			<!-- B25: the table scrolls inside this wrapper, so its header can stick. -->
			<div class="table-responsive licence-scroll">
				<table class="table table-sm table-striped table-hover align-middle mb-0">
					<thead class="table-dark">
						<tr class="small text-uppercase text-body-secondary">
							<th scope="col">Component</th>
							<th scope="col">Licence</th>
						</tr>
					</thead>
					<tbody>
						{#each licenses as license (license.name)}
							<tr>
								<td>
									<!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- external licence URL -->
									<a href={license.url} rel="noopener noreferrer" target="_blank">{license.name}</a>
								</td>
								<td>{license.license}</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		</div>
	{/if}
</ConfirmModal>

<style>
	.licence-scroll {
		max-height: 40vh;
		overflow-y: auto;
	}
	/* :global keeps the table head class exactly `table-dark` (table-style.test.ts). */
	.licence-scroll :global(thead th) {
		position: sticky;
		top: 0;
		z-index: 1;
	}
</style>
