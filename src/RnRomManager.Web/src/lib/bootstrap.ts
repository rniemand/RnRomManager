// Bootstrap's own JS (spec §11): navbar collapse and dropdowns through the data API, modal focus trapping through
// `Modal`. The UMD bundle includes Popper, so there's no extra dependency. Imported only in the browser (layout, modal).
import bootstrap from 'bootstrap/dist/js/bootstrap.bundle.min.js';

export const Modal = bootstrap.Modal;
