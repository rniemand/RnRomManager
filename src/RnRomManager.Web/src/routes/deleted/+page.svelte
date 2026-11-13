<script lang="ts">
	import AdminOnly from '#lib/components/AdminOnly.svelte';
	import Icon from '#lib/components/Icon.svelte';
</script>

<!-- shortcut: placeholder so the nav.deleted hotkey has a route; the Deleted ROMs page (spec D3) replaces it. -->
<svelte:head><title>Deleted ROMs · RnRomManager</title></svelte:head>

<AdminOnly>
	<h1 class="h3 mb-3"><Icon name="delete" /> Deleted ROMs</h1>
</AdminOnly>
