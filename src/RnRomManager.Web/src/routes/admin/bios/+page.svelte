<script lang="ts">
	import { onMount } from 'svelte';
	import { api } from '#lib/api/client.js';
	import type { BiosFileStatus, BiosGroup, BiosInfo } from '#lib/types/index.js';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import Icon from '#lib/icons/Icon.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
	import { formatSize } from '#lib/format.js';
	import { auth } from '#lib/stores/auth.svelte.js';

	let info = $state<BiosInfo | null>(null);
	let error = $state<unknown>(null);

	async function load(): Promise<void> {
		error = null;
		try {
			info = await api.get<BiosInfo>('/api/admin/bios');
		} catch (e) {
			error = e;
		}
	}

	onMount(() => {
		if (auth.isAdmin) void load();
	});

	const needLabel: Record<BiosGroup['need'], string> = {
		All: 'Required',
		OneOf: 'One of these required',
		Optional: 'Optional'
	};

	const statusBadge: Record<BiosFileStatus['status'], { label: string; badge: string }> = {
		Valid: { label: 'Valid', badge: 'text-bg-success' },
		Missing: { label: 'Missing', badge: 'text-bg-secondary' },
		WrongSize: { label: 'Wrong size', badge: 'text-bg-danger' },
		Unsafe: { label: 'Unsafe (link or duplicate name)', badge: 'text-bg-danger' },
		Unreadable: { label: "Can't be read", badge: 'text-bg-danger' }
	};

	function expected(file: BiosFileStatus): string {
		if (file.minBytes === file.maxBytes) return formatSize(file.minBytes);
		return file.minBytes > 1
			? `at least ${formatSize(file.minBytes)}`
			: `up to ${formatSize(file.maxBytes)}`;
	}
</script>

<svelte:head><title>BIOS info · Admin · RnRomManager</title></svelte:head>

{#if error}
	<ErrorAlert {error} onretry={load} />
{:else if !info}
	<Loading />
{:else}
	<div class="card card-body mb-3">
		<p class="mb-2">
			Browser play loads BIOS files from the BIOS folder. Copy them in yourself; the app ships none.
			Names are matched ignoring case. Any file in the folder can be downloaded by a signed-in user,
			so keep only BIOS files there.
		</p>
		<dl class="row mb-0 small">
			<dt class="col-sm-3">Folder</dt>
			<dd class="col-sm-9">
				<code>{info.folder}</code>
				{#if !info.folderExists}<span class="badge text-bg-danger ms-1">Not found</span>{/if}
			</dd>
			<dt class="col-sm-3">Setting</dt>
			<dd class="col-sm-9"><code>{info.setting}</code></dd>
			<dt class="col-sm-3">Docker environment variable</dt>
			<dd class="col-sm-9">
				<code>{info.env}</code> (default <code>/app/bios</code>; mount it, e.g.
				<code>- ./data/bios:/app/bios</code>)
			</dd>
		</dl>
	</div>

	<div class="card mb-3">
		<div class="table-responsive">
			<table class="table table-sm table-striped table-hover align-middle mb-0">
				<thead class="table-dark">
					<tr>
						<th scope="col">System</th>
						<th scope="col">File</th>
						<th scope="col">Expected size</th>
						<th scope="col">Status</th>
						<th scope="col">Configuration</th>
					</tr>
				</thead>
				<tbody>
					{#each info.groups as group (group.system)}
						{@const rows = Math.max(group.files.length, 1)}
						{#each group.files.length ? group.files : [null] as file, i (file?.name ?? '')}
							<tr>
								{#if i === 0}
									<th scope="row" rowspan={rows}>
										<span class="d-flex align-items-center gap-1">
											<Icon
												name={group.ok ? 'integrity' : 'clear'}
												label={group.ok ? 'Ready' : 'Needs attention'}
												class={group.ok ? 'text-success' : 'text-danger'}
											/>
											{group.system}
										</span>
										<div class="small fw-normal text-body-secondary">
											{needLabel[group.need]}. {group.note}
										</div>
									</th>
								{/if}
								{#if file}
									<td><code>{file.name}</code></td>
									<td>{expected(file)}</td>
									<td>
										<span class="badge {statusBadge[file.status].badge}"
											>{statusBadge[file.status].label}</span
										>
										{#if file.size}<span class="small text-body-secondary ms-1"
												>{formatSize(file.size)}</span
											>{/if}
										{#if file.md5}<div class="small text-body-secondary text-break">
												MD5 <code>{file.md5}</code>
											</div>{/if}
									</td>
								{:else}
									<td colspan="3" class="text-body-secondary">—</td>
								{/if}
								{#if i === 0}
									<td rowspan={rows} class="small">
										<code>{info.env}</code>
										{#if group.coreEnv}
											<div>
												<code>{group.coreEnv}</code>
												{#if group.coreConfigured}
													<span class="badge text-bg-success">Set</span>
												{:else}
													<span class="badge text-bg-danger">Not set</span>
												{/if}
											</div>
										{/if}
									</td>
								{/if}
							</tr>
						{/each}
					{/each}
				</tbody>
			</table>
		</div>
	</div>

	<h2 class="h6">Other files</h2>
	{#if info.otherFiles.length}
		<ul class="list-unstyled small">
			{#each info.otherFiles as file (file.name)}
				<li>
					<code>{file.name}</code> <span class="text-body-secondary">{formatSize(file.size)}</span>
				</li>
			{/each}
		</ul>
	{:else}
		<p class="small text-body-secondary">None.</p>
	{/if}
{/if}
