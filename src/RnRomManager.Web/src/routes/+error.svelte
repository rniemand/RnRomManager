<script lang="ts">
	import { page } from '$app/state';
	import { resolve } from '$app/paths';
	import Icon from '#lib/components/Icon.svelte';

	const notFound = $derived(page.status === 404);
</script>

<div class="text-center py-5">
	<Icon
		name={notFound ? 'not-found' : 'error'}
		class="display-4 text-body-secondary d-block mb-3"
	/>
	<h1 class="h3">{notFound ? 'Page not found' : 'Something went wrong'}</h1>
	<p class="text-body-secondary">
		{notFound ? "There's nothing at this address." : page.error?.message}
	</p>
	<a class="btn btn-primary" href={resolve('/')}>Go to the Library</a>
</div>
