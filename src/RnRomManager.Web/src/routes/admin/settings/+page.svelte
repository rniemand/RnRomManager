<script lang="ts">
	import { onMount } from 'svelte';
	import { api } from '#lib/api/client.js';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import { flattenSettings, formatValue } from '#lib/format.js';
	import { auth } from '#lib/stores/auth.svelte.js';

	let settings = $state<[string, unknown][] | null>(null);
	let error = $state<unknown>(null);

	async function load(): Promise<void> {
		error = null;
		try {
			settings = flattenSettings(await api.get<Record<string, unknown>>('/api/admin/settings'));
		} catch (e) {
			error = e;
		}
	}

	onMount(() => {
		if (auth.isAdmin) void load();
	});
</script>

<svelte:head><title>Settings · Admin · RnRomManager</title></svelte:head>

<p class="text-body-secondary">
	The effective configuration, read-only, with secrets masked. Change settings in
	<code>appsettings.json</code> or environment variables and restart.
</p>

<!-- The API sends the ZIP as an attachment, so a plain link downloads it. -->
<!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- API endpoint, not an app route -->
<a class="btn btn-outline-primary mb-3" href="/api/admin/logs" download>
	<Icon name="download" /> Download logs
</a>

{#if error}
	<ErrorAlert {error} onretry={load} />
{:else if !settings}
	<Loading />
{:else}
	<div class="card">
		<div class="table-responsive">
			<table class="table table-sm table-hover align-middle mb-0">
				<thead>
					<tr class="small text-uppercase text-body-secondary">
						<th scope="col">Setting</th>
						<th scope="col">Value</th>
					</tr>
				</thead>
				<tbody>
					{#each settings as [key, value] (key)}
						<tr>
							<th scope="row" class="fw-normal text-body-secondary text-nowrap">{key}</th>
							<td class="text-break font-monospace small">{formatValue(value)}</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
	</div>
{/if}
