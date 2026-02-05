---
page_id: test-payment-trained
url_pattern: /demo/payment*
title: Payment Details (Trained)
learned: 2026-02-05T12:57:46+00:00
site: localhost
---

# Payment Details (Trained)

## Fields

### [#card-number] Card Number
- type: text
- pattern: [0-9]{13,19}
- required: true
- autocomplete: cc-number
- help: Enter your 16-digit card number

### [#expiry] Expiry Date
- type: text
- pattern: (0[1-9]|1[0-2])/[0-9]{2}
- required: true
- autocomplete: cc-exp
- help: Month and year (MM/YY)

### [#cvv] CVV
- type: text
- pattern: [0-9]{3,4}
- required: true
- autocomplete: cc-csc
- help: 3 or 4 digit security code

### [#billing-address] Billing Address
- type: text
- required: true
- autocomplete: street-address

### [#city] City
- type: text
- required: true

### [#postal-code] Postal Code
- type: text
- pattern: [0-9]{5}(-[0-9]{4})?
- required: true
- autocomplete: postal-code
