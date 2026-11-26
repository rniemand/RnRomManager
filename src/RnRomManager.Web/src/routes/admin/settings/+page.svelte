<script lang="ts">
	import DataTable from '#lib/components/common/DataTable.svelte';
	import { onMount } from 'svelte';
	import { api } from '#lib/api/client.js';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
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

{#if error}
	<ErrorAlert {error} onretry={load} />
{:else if !settings}
	<Loading />
{:else}
	<DataTable items={settings} key={([key]) => key}>
		{#snippet head()}
			<tr class="small text-uppercase text-body-secondary">
				<th scope="col">Setting</th>
				<th scope="col">Value</th>
			</tr>
		{/snippet}
		{#snippet row([key, value])}
			<tr>
				<th scope="row" class="fw-normal text-body-secondary text-nowrap">{key}</th>
				<td class="text-break font-monospace small">{formatValue(value)}</td>
			</tr>
		{/snippet}
	</DataTable>
{/if}
