<script lang="ts">
	import { onMount } from 'svelte';
	import { api } from '#lib/api/client.js';
	import type { About } from '#lib/api/types.js';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import { auth } from '#lib/stores/auth.svelte.js';

	const repoUrl = 'https://github.com/rniemand/RnRomManager';

	let about = $state<About | null>(null);
	let error = $state<unknown>(null);

	async function load(): Promise<void> {
		error = null;
		try {
			about = await api.get<About>('/api/about');
		} catch (e) {
			error = e;
		}
	}

	onMount(() => {
		if (auth.isAdmin) void load();
	});
</script>

<svelte:head><title>About · Admin · RnRomManager</title></svelte:head>

{#if error}
	<ErrorAlert {error} onretry={load} />
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
	<div class="card">
		<div class="table-responsive">
			<table class="table table-sm table-hover align-middle mb-0">
				<thead>
					<tr class="small text-uppercase text-body-secondary">
						<th scope="col">Component</th>
						<th scope="col">Licence</th>
					</tr>
				</thead>
				<tbody>
					{#each about.licenses as license (license.name)}
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
