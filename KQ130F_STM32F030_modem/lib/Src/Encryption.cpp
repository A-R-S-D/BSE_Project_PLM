/*
 * Encryption.cpp
 *
 *  Created on: Jul 29, 2022
 *      Author: lidoma
 */

#include "Encryption.h"
#include "primes.h"

Enc::RSA::RSA() {}
Enc::RSA::RSA(RSAKey_t& pub, RSAKey_t& priv) {}
void Enc::RSA::encrypt(const uint8_t* plain, const size_t pLen,
									uint8_t* enc, size_t& eLen) {}
void Enc::RSA::decrypt(const uint8_t* enc, const size_t eLen,
									uint8_t* plain, size_t& pLen) {}

Enc::Encryption::Encryption() {}
Enc::Encryption::~Encryption() {}
void Enc::Encryption::encrypt(const uint8_t* plain, const size_t pLen,
									uint8_t* enc, size_t& eLen) {}
void Enc::Encryption::decrypt(const uint8_t* enc, const size_t eLen,
									uint8_t* plain, size_t& pLen) {}

